using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiStocks.Core;
using AiStocks.Research.Decisions;
using AngleSharp;
using AngleSharp.Dom;

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
    public EvidenceHttpResponse(HttpStatusCode statusCode, IReadOnlyDictionary<string, string> headers, Stream content,
        IAsyncDisposable? owner = null, IPAddress? pinnedAddress = null)
    {
        StatusCode = statusCode;
        Headers = headers;
        Content = content;
        PinnedAddress = pinnedAddress;
        _owner = owner;
    }
    public HttpStatusCode StatusCode { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public Stream Content { get; }
    public IPAddress? PinnedAddress { get; }
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
        IPAddress? connectedAddress = null;
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
                        connectedAddress = address;
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
            return new EvidenceHttpResponse(response.StatusCode, headers, stream,
                new HttpOwner(response, request, client), connectedAddress);
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
        var verificationStartedAt = DateTimeOffset.UtcNow;
        var hops = ImmutableArray.CreateBuilder<EvidenceRetrievalHop>();
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
                var receivedAt = DateTimeOffset.UtcNow;
                Uri? redirectTarget = null;
                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= _options.MaximumRedirects) throw new EvidenceVerificationException("Evidence redirect limit exceeded.");
                    if (!response.Headers.TryGetValue("Location", out var location) || !Uri.TryCreate(current, location, out redirectTarget))
                        throw new EvidenceVerificationException("Evidence redirect has an invalid Location header.");
                    ValidateUri(redirectTarget);
                }

                var responseHeaders = response.Headers.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
                hops.Add(new EvidenceRetrievalHop(current, addresses.ToImmutableArray(),
                    response.PinnedAddress ?? addresses[0], (int)response.StatusCode, redirectTarget,
                    responseHeaders, receivedAt));
                if (redirectTarget is not null)
                {
                    current = redirectTarget;
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                    throw new EvidenceVerificationException($"Evidence endpoint returned HTTP {(int)response.StatusCode}.");
                if (!response.Headers.TryGetValue("Content-Type", out var contentType) || !IsTextualContentType(contentType) ||
                    (response.Headers.TryGetValue("Content-Encoding", out var contentEncoding) &&
                     !string.IsNullOrWhiteSpace(contentEncoding) && !contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new EvidenceVerificationException("Evidence response is not an identity-encoded textual representation.");

                var bytes = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
                var retrievedAt = DateTimeOffset.UtcNow;
                var text = DecodeText(bytes, response.Headers);
                var mediaType = contentType.Split(';', 2)[0].Trim();
                var visibleLines = await ExtractVisibleLinesAsync(text, mediaType, deadline.Token).ConfigureAwait(false);
                var excerpt = NormalizeWhitespace(WebUtility.HtmlDecode(claim.ExactExcerpt));
                if (excerpt.Length == 0 || !visibleLines.Any(line => line.Contains(excerpt, StringComparison.Ordinal)))
                    throw new EvidenceVerificationException("The exact excerpt was not found in fetched visible text.");
                var publishedAt = await ParsePublicationTimeAsync(text, mediaType, deadline.Token).ConfigureAwait(false);
                if (publishedAt != claim.PublishedAt || publishedAt > retrievedAt)
                    throw new EvidenceVerificationException("Claimed publication time does not match independently fetched source metadata.");

                return new VerifiedEvidence(current, publishedAt, retrievedAt,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), excerpt)
                {
                    OriginalUrl = claim.Url,
                    VerificationStartedAt = verificationStartedAt,
                    Hops = hops.ToImmutable(),
                    ResponseHeaders = responseHeaders,
                    ContentType = contentType,
                    ImmutableContent = bytes.ToImmutableArray()
                };
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
            ReadOnlySpan<(uint Network, int Bits)> nonPublic =
            [
                (0x00000000, 8), (0x0a000000, 8), (0x64400000, 10), (0x7f000000, 8),
                (0xa9fe0000, 16), (0xac100000, 12), (0xc0000000, 24), (0xc0000200, 24),
                (0xc01fc400, 24), (0xc034c100, 24), (0xc0586300, 24), (0xc0a80000, 16),
                (0xc0af3000, 24), (0xc6120000, 15), (0xc6336400, 24), (0xcb007100, 24),
                (0xe0000000, 4), (0xf0000000, 4)
            ];
            var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            foreach (var (network, bits) in nonPublic)
            {
                var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
                if ((value & mask) == network) return false;
            }
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        // Global unicast is 2000::/3. Conservatively reject every IANA special-purpose
        // sub-prefix that can otherwise look globally scoped.
        if ((bytes[0] & 0xe0) != 0x20) return false;
        if (InPrefix(bytes, [0x20, 0x01, 0x00], 23) ||
            InPrefix(bytes, [0x20, 0x01, 0x0d, 0xb8], 32) ||
            InPrefix(bytes, [0x20, 0x02], 16) ||
            InPrefix(bytes, [0x3f, 0xff, 0x00], 20))
            return false;
        return true;
    }

    private static bool InPrefix(ReadOnlySpan<byte> address, ReadOnlySpan<byte> prefix, int bits)
    {
        var wholeBytes = bits / 8;
        if (!address[..wholeBytes].SequenceEqual(prefix[..wholeBytes])) return false;
        var remaining = bits % 8;
        if (remaining == 0) return true;
        var mask = (byte)(0xff << (8 - remaining));
        return (address[wholeBytes] & mask) == (prefix[wholeBytes] & mask);
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

    private static string DecodeText(byte[] bytes, IReadOnlyDictionary<string, string> headers)
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

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "div", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "li", "main", "nav", "ol", "p", "pre", "section", "table", "td", "th", "tr", "ul"
    };

    private static async Task<IReadOnlyList<string>> ExtractVisibleLinesAsync(
        string text, string mediaType, CancellationToken cancellationToken)
    {
        if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            var context = BrowsingContext.New(Configuration.Default.WithCss());
            using var document = await context.OpenAsync(request => request.Content(text), cancellationToken).ConfigureAwait(false);
            if (document.QuerySelectorAll("link[rel]").Any(link =>
                (link.GetAttribute("rel") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("stylesheet", StringComparer.OrdinalIgnoreCase)))
                throw new EvidenceVerificationException("Evidence HTML depends on an external stylesheet, so visible text cannot be proven from the retained representation.");
            var roots = new List<IElement>();
            if (document.Body is not null) roots.Add(document.Body);
            roots.AddRange(document.QuerySelectorAll(string.Join(',', BlockElements)));
            var lines = new List<string>();
            foreach (var root in roots.Distinct())
            {
                if (IsHidden(root)) continue;
                var builder = new StringBuilder();
                AppendVisibleText(root, root, builder);
                var line = NormalizeWhitespace(builder.ToString());
                if (line.Length > 0) lines.Add(line);
            }
            return lines;
        }

        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
                var lines = new List<string>();
                CollectJsonStrings(document.RootElement, lines);
                return lines.Select(NormalizeWhitespace).Where(value => value.Length > 0).ToArray();
            }
            catch (JsonException exception)
            {
                throw new EvidenceVerificationException("Evidence JSON is malformed.", exception);
            }
        }

        if (mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var document = System.Xml.Linq.XDocument.Parse(text, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                return document.DescendantNodes().OfType<System.Xml.Linq.XText>()
                    .Select(node => NormalizeWhitespace(node.Value)).Where(value => value.Length > 0).ToArray();
            }
            catch (System.Xml.XmlException exception)
            {
                throw new EvidenceVerificationException("Evidence XML is malformed.", exception);
            }
        }

        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeWhitespace).Where(value => value.Length > 0).ToArray();
    }

    private static bool IsHidden(IElement element)
    {
        for (var current = element; current is not null; current = current.ParentElement)
        {
            if (current.HasAttribute("hidden") ||
                current.GetAttribute("aria-hidden")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true ||
                current.LocalName is "script" or "style" or "noscript" or "template" or "svg" or "head")
                return true;
            var style = current.ComputeCurrentStyle();
            if (style is not null &&
                (style.GetPropertyValue("display").Equals("none", StringComparison.OrdinalIgnoreCase) ||
                 style.GetPropertyValue("visibility") is "hidden" or "collapse" ||
                 style.GetPropertyValue("content-visibility").Equals("hidden", StringComparison.OrdinalIgnoreCase) ||
                 style.GetPropertyValue("opacity") == "0"))
                return true;
        }
        return false;
    }

    private static void AppendVisibleText(INode node, IElement root, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText text)
            {
                builder.Append(text.Data).Append(' ');
            }
            else if (child is IElement element && !IsHidden(element))
            {
                if (!ReferenceEquals(element, root) && BlockElements.Contains(element.LocalName)) continue;
                if (element.LocalName == "br") builder.Append(' ');
                AppendVisibleText(element, root, builder);
            }
        }
    }

    private static void CollectJsonStrings(JsonElement element, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) CollectJsonStrings(property.Value, values);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectJsonStrings(item, values);
                break;
            case JsonValueKind.String:
                values.Add(element.GetString() ?? string.Empty);
                break;
        }
    }

    private static string NormalizeWhitespace(string value) => WhitespaceRegex().Replace(value, " ").Trim();

    private static async Task<DateTimeOffset> ParsePublicationTimeAsync(
        string text, string mediaType, CancellationToken cancellationToken)
    {
        var values = new HashSet<DateTimeOffset>();
        if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            var context = BrowsingContext.New(Configuration.Default);
            using var document = await context.OpenAsync(request => request.Content(text), cancellationToken).ConfigureAwait(false);
            foreach (var meta in document.QuerySelectorAll("meta"))
            {
                var key = meta.GetAttribute("property") ?? meta.GetAttribute("name") ?? meta.GetAttribute("itemprop");
                if (key is not null && (key.Equals("article:published_time", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("datePublished", StringComparison.OrdinalIgnoreCase) || key.Equals("date", StringComparison.OrdinalIgnoreCase)))
                    AddTimestamp(values, meta.GetAttribute("content") ?? string.Empty);
            }
            foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
                AddJsonPublicationTimes(values, script.TextContent);
        }
        else if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            AddJsonPublicationTimes(values, text);
        }
        else if (mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var document = System.Xml.Linq.XDocument.Parse(text);
                foreach (var element in document.Descendants().Where(element =>
                    element.Name.LocalName.Equals("datePublished", StringComparison.OrdinalIgnoreCase) ||
                    element.Name.LocalName.Equals("published", StringComparison.OrdinalIgnoreCase)))
                    AddTimestamp(values, element.Value);
            }
            catch (System.Xml.XmlException exception)
            {
                throw new EvidenceVerificationException("Evidence XML is malformed.", exception);
            }
        }
        if (values.Count != 1) throw new EvidenceVerificationException("Fetched source lacks one unambiguous independently verifiable publication time.");
        return values.Single();
    }

    private static void AddJsonPublicationTimes(HashSet<DateTimeOffset> values, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            Visit(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new EvidenceVerificationException("Fetched publication metadata contains malformed JSON.", exception);
        }

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals("datePublished", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        AddTimestamp(values, property.Value.GetString() ?? string.Empty);
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Visit(item);
            }
        }
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
    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)] private static partial Regex WhitespaceRegex();
}
