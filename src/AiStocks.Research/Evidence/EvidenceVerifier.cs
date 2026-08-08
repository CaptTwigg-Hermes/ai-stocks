using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AiStocks.Core;
using AiStocks.Research.Decisions;

namespace AiStocks.Research.Evidence;

public sealed record EvidenceVerificationOptions
{
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumResponseBytes { get; init; } = 2 * 1024 * 1024;
    public int MaximumRedirects { get; init; } = 5;
}

public sealed class EvidenceVerificationException : Exception
{
    public EvidenceVerificationException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public interface IHostResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemHostResolver : IHostResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}

public interface IEvidenceHttpTransport
{
    Task<EvidenceHttpResponse> SendAsync(Uri uri, IReadOnlyList<IPAddress> approvedAddresses, CancellationToken cancellationToken);
}

public sealed class EvidenceHttpResponse : IAsyncDisposable
{
    private readonly IAsyncDisposable? _owner;
    public EvidenceHttpResponse(HttpStatusCode statusCode, IReadOnlyDictionary<string, string> headers, Stream content, IAsyncDisposable? owner = null)
    {
        StatusCode = statusCode;
        Headers = headers;
        Content = content;
        _owner = owner;
    }
    public HttpStatusCode StatusCode { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public Stream Content { get; }
    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        if (_owner is not null) await _owner.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Connects only to prevalidated IPs while retaining the URI hostname for HTTP Host and TLS SNI/certificate validation.</summary>
public sealed class PinnedAddressHttpTransport : IEvidenceHttpTransport
{
    private readonly TimeSpan _connectTimeout;
    public PinnedAddressHttpTransport(TimeSpan? connectTimeout = null) => _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);

    public async Task<EvidenceHttpResponse> SendAsync(Uri uri, IReadOnlyList<IPAddress> approvedAddresses, CancellationToken cancellationToken)
    {
        if (approvedAddresses.Count == 0) throw new EvidenceVerificationException("No approved destination address was supplied.");
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = _connectTimeout,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, token) =>
            {
                if (!context.DnsEndPoint.Host.Equals(uri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                    context.DnsEndPoint.Port != uri.Port)
                    throw new EvidenceVerificationException("Transport attempted to change the pinned HTTPS authority.");
                Exception? last = null;
                foreach (var address in approvedAddresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                    {
                        socket.Dispose();
                        last = exception;
                        if (exception is OperationCanceledException) throw;
                    }
                }
                throw new HttpRequestException("Unable to connect to an approved evidence address.", last);
            }
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("AiStocks-EvidenceVerifier/1.0");
        request.Headers.Accept.ParseAdd("text/html, application/xhtml+xml, text/plain, application/json, application/xml");
        request.Headers.AcceptEncoding.ParseAdd("identity");
        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var headers = response.Headers.Concat(response.Content.Headers)
                .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => string.Join(",", group.SelectMany(value => value.Value)), StringComparer.OrdinalIgnoreCase);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new EvidenceHttpResponse(response.StatusCode, headers, stream, new HttpOwner(response, request, client));
        }
        catch
        {
            request.Dispose();
            client.Dispose();
            throw;
        }
    }

    private sealed class HttpOwner(HttpResponseMessage response, HttpRequestMessage request, HttpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            response.Dispose();
            request.Dispose();
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public interface IEvidenceVerifier
{
    Task<VerifiedEvidence> VerifyAsync(EvidenceClaim claim, CancellationToken cancellationToken);
}

public sealed partial class EvidenceVerifier : IEvidenceVerifier
{
    private readonly IHostResolver _resolver;
    private readonly IEvidenceHttpTransport _transport;
    private readonly EvidenceVerificationOptions _options;

    public EvidenceVerifier(IHostResolver resolver, IEvidenceHttpTransport transport, EvidenceVerificationOptions? options = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new EvidenceVerificationOptions();
        if (_options.Deadline <= TimeSpan.Zero || _options.ConnectTimeout <= TimeSpan.Zero ||
            _options.MaximumResponseBytes <= 0 || _options.MaximumRedirects < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public async Task<VerifiedEvidence> VerifyAsync(EvidenceClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ValidateUri(claim.Url);
        if (string.IsNullOrWhiteSpace(claim.ExactExcerpt)) throw new EvidenceVerificationException("Evidence excerpt cannot be empty.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Deadline);
        try
        {
            var current = claim.Url;
            for (var redirects = 0; ; redirects++)
            {
                ValidateUri(current);
                var addresses = await ResolveAndValidateAsync(current.IdnHost, deadline.Token).ConfigureAwait(false);
                await using var response = await _transport.SendAsync(current, addresses, deadline.Token).ConfigureAwait(false);
                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= _options.MaximumRedirects) throw new EvidenceVerificationException("Evidence redirect limit exceeded.");
                    if (!response.Headers.TryGetValue("Location", out var location) || !Uri.TryCreate(current, location, out var next))
                        throw new EvidenceVerificationException("Evidence redirect has an invalid Location header.");
                    ValidateUri(next);
                    current = next;
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new EvidenceVerificationException($"Evidence endpoint returned HTTP {(int)response.StatusCode}.");
                if (!response.Headers.TryGetValue("Content-Type", out var contentType) || !IsTextualContentType(contentType) ||
                    (response.Headers.TryGetValue("Content-Encoding", out var contentEncoding) &&
                     !string.IsNullOrWhiteSpace(contentEncoding) && !contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new EvidenceVerificationException("Evidence response is not an identity-encoded textual representation.");

                var retrievedAt = DateTimeOffset.UtcNow;
                var bytes = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
                var html = DecodeHtml(bytes, response.Headers);
                var visibleLines = ExtractVisibleLines(html);
                var excerpt = NormalizeWhitespace(WebUtility.HtmlDecode(claim.ExactExcerpt));
                if (excerpt.Length == 0 || !visibleLines.Any(line => line.Contains(excerpt, StringComparison.Ordinal)))
                    throw new EvidenceVerificationException("The exact excerpt was not found in fetched visible text.");
                var publishedAt = ParsePublicationTime(html);
                if (publishedAt != claim.PublishedAt || publishedAt > retrievedAt)
                    throw new EvidenceVerificationException("Claimed publication time does not match independently fetched source metadata.");

                return new VerifiedEvidence(current, publishedAt, retrievedAt,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), claim.ExactExcerpt);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EvidenceVerificationException("Evidence verification exceeded its overall deadline.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new EvidenceVerificationException("Evidence transport failed.", exception);
        }
        catch (SocketException exception)
        {
            throw new EvidenceVerificationException("Evidence DNS or network lookup failed.", exception);
        }
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveAndValidateAsync(string host, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else addresses = await _resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new EvidenceVerificationException("Evidence host did not resolve exclusively to public addresses.");
        return addresses.Distinct().ToArray();
    }

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.HostNameType == UriHostNameType.Unknown ||
            (uri.HostNameType == UriHostNameType.Dns && !uri.Host.Contains('.', StringComparison.Ordinal)) ||
            uri.Port != 443 || !string.IsNullOrEmpty(uri.Fragment) || uri.Host.Contains('*', StringComparison.Ordinal) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw new EvidenceVerificationException("Evidence URL must identify a public HTTPS host without credentials.");
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0]; var b = bytes[1]; var c = bytes[2];
            return !(a == 0 || a == 10 || a == 127 || a >= 224 ||
                (a == 100 && b is >= 64 and <= 127) || (a == 169 && b == 254) ||
                (a == 172 && b is >= 16 and <= 31) || (a == 192 && b == 168) ||
                (a == 192 && b == 0 && c is 0 or 2) || (a == 198 && b is 18 or 19 or 51) ||
                (a == 203 && b == 0 && c == 113));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        return !(address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) ||
            address.IsIPv6LinkLocal || address.IsIPv6Multicast || (bytes[0] & 0xfe) == 0xfc ||
            (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8));
    }

    private async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(_options.MaximumResponseBytes, 81920));
        var buffer = new byte[Math.Min(_options.MaximumResponseBytes + 1, 81920)];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > _options.MaximumResponseBytes)
                throw new EvidenceVerificationException("Evidence response exceeded its byte bound.");
            output.Write(buffer, 0, read);
        }
    }

    private static string DecodeHtml(byte[] bytes, IReadOnlyDictionary<string, string> headers)
    {
        var charset = "utf-8";
        if (headers.TryGetValue("Content-Type", out var contentType))
        {
            var match = CharsetRegex().Match(contentType);
            if (match.Success) charset = match.Groups[1].Value.Trim('"', '\'');
        }
        try
        {
            var encoding = Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return encoding.GetString(bytes);
        }
        catch (Exception exception) when (exception is ArgumentException or DecoderFallbackException)
        {
            throw new EvidenceVerificationException("Evidence content encoding is invalid or unsupported.", exception);
        }
    }

    private static IReadOnlyList<string> ExtractVisibleLines(string html)
    {
        var withoutHidden = HiddenElementRegex().Replace(html, " ");
        withoutHidden = CommentRegex().Replace(withoutHidden, " ");
        withoutHidden = BlockTagRegex().Replace(withoutHidden, "\n");
        withoutHidden = TagRegex().Replace(withoutHidden, string.Empty);
        return WebUtility.HtmlDecode(withoutHidden).Split('\n')
            .Select(NormalizeWhitespace).Where(line => line.Length > 0).ToArray();
    }
    private static string NormalizeWhitespace(string value) => WhitespaceRegex().Replace(value, " ").Trim();

    private static DateTimeOffset ParsePublicationTime(string html)
    {
        var values = new HashSet<DateTimeOffset>();
        foreach (Match tag in MetaTagRegex().Matches(html))
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in AttributeRegex().Matches(tag.Value))
            {
                var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[4].Value;
                var value = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[5].Value;
                if (!attributes.TryAdd(name, value))
                    throw new EvidenceVerificationException("Fetched publication metadata contains duplicate attributes.");
            }
            var key = attributes.GetValueOrDefault("property") ?? attributes.GetValueOrDefault("name") ?? attributes.GetValueOrDefault("itemprop");
            if (key is not null && (key.Equals("article:published_time", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("datePublished", StringComparison.OrdinalIgnoreCase) || key.Equals("date", StringComparison.OrdinalIgnoreCase)) &&
                attributes.TryGetValue("content", out var content))
                AddTimestamp(values, WebUtility.HtmlDecode(content));
        }
        foreach (Match match in JsonDatePublishedRegex().Matches(html)) AddTimestamp(values, WebUtility.HtmlDecode(match.Groups[1].Value));
        if (values.Count != 1) throw new EvidenceVerificationException("Fetched source lacks one unambiguous independently verifiable publication time.");
        return values.Single();
    }
    private static void AddTimestamp(HashSet<DateTimeOffset> values, string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)) values.Add(timestamp);
    }
    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
        HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static bool IsTextualContentType(string value)
    {
        var mediaType = value.Split(';', 2)[0].Trim();
        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("charset\\s*=\\s*([^;\\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CharsetRegex();
    [GeneratedRegex("<(script|style|noscript|template|svg)\\b[^>]*>.*?</\\1\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)] private static partial Regex HiddenElementRegex();
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)] private static partial Regex CommentRegex();
    [GeneratedRegex("</?(address|article|aside|blockquote|br|div|footer|h[1-6]|header|hr|li|main|nav|ol|p|pre|section|table|td|th|tr|ul)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex BlockTagRegex();
    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline | RegexOptions.CultureInvariant)] private static partial Regex TagRegex();
    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)] private static partial Regex WhitespaceRegex();
    [GeneratedRegex("<meta\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex MetaTagRegex();
    [GeneratedRegex("([\\w:-]+)\\s*=\\s*([\"'])(.*?)\\2|([\\w:-]+)\\s*=\\s*([^\\s>]+)", RegexOptions.Singleline | RegexOptions.CultureInvariant)] private static partial Regex AttributeRegex();
    [GeneratedRegex("\"datePublished\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex JsonDatePublishedRegex();
}
