using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AiStocks.Web;

public sealed class AccessOptions
{
    public const string Section = "Access";
    public string TeamDomain { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string PublicOrigin { get; set; } = string.Empty;
    public string[] OwnerEmails { get; set; } = [];
    public string[] ViewerEmails { get; set; } = [];
    public TimeSpan JwksCacheTtl { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan RefreshCooldown { get; set; } = TimeSpan.FromSeconds(30);
}

public interface IAccessAssertionValidator
{
    Task<AccessIdentity> ValidateAsync(string assertion, CancellationToken cancellationToken);
}

public sealed record AccessIdentity(string Email, string Role);

public interface IJwksFetcher
{
    Task<byte[]> FetchAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class BoundedJwksFetcher(HttpClient client) : IJwksFetcher
{
    public const int MaximumBytes = 262_144;

    public async Task<byte[]> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumBytes) throw new InvalidDataException("JWKS is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumBytes) throw new InvalidDataException("JWKS is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

public sealed class CloudflareAccessValidator : IAccessAssertionValidator, IDisposable
{
    public const int MaximumAssertionBytes = 16_384;
    private readonly IJwksFetcher _fetcher;
    private readonly TimeProvider _clock;
    private readonly Uri _issuer;
    private readonly Uri _jwksUri;
    private readonly string _audience;
    private readonly string _publicOrigin;
    private readonly HashSet<string> _owners;
    private readonly HashSet<string> _viewers;
    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _refreshCooldown;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyDictionary<string, RSA> _keys = new Dictionary<string, RSA>();
    private DateTimeOffset _expiresAt;
    private DateTimeOffset _lastRefreshAttempt = DateTimeOffset.MinValue;

    public CloudflareAccessValidator(IOptions<AccessOptions> options, IJwksFetcher fetcher, TimeProvider clock)
    {
        _fetcher = fetcher;
        _clock = clock;
        var value = options.Value;
        _issuer = ValidateOrigin(value.TeamDomain, true);
        _jwksUri = new Uri(_issuer, "/cdn-cgi/access/certs");
        _ = ValidateOrigin(value.PublicOrigin, false);
        _publicOrigin = value.PublicOrigin.TrimEnd('/');
        _audience = value.Audience.Trim();
        if (string.IsNullOrWhiteSpace(_audience) || _audience == "*" || _audience.Length > 512) throw new OptionsValidationException(AccessOptions.Section, typeof(AccessOptions), ["Audience must be exact."]);
        _owners = NormalizeAllowlist(value.OwnerEmails, "owner");
        _viewers = NormalizeAllowlist(value.ViewerEmails, "viewer");
        if (_owners.Overlaps(_viewers)) throw new OptionsValidationException(AccessOptions.Section, typeof(AccessOptions), ["Allowlist roles overlap."]);
        if (value.JwksCacheTtl <= TimeSpan.Zero || value.RefreshCooldown <= TimeSpan.Zero) throw new OptionsValidationException(AccessOptions.Section, typeof(AccessOptions), ["Cache durations must be positive."]);
        _cacheTtl = value.JwksCacheTtl;
        _refreshCooldown = value.RefreshCooldown;
    }

    public string PublicOrigin => _publicOrigin;

    public async Task<AccessIdentity> ValidateAsync(string assertion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(assertion) || Encoding.UTF8.GetByteCount(assertion) > MaximumAssertionBytes) throw new AuthenticationFailureException("Invalid Access assertion.");
        var parts = assertion.Split('.');
        if (parts.Length != 3) throw new AuthenticationFailureException("Invalid Access assertion.");
        using var header = ParsePart(parts[0]);
        if (header.RootElement.ValueKind != JsonValueKind.Object || GetRequiredString(header.RootElement, "alg") != "RS256") throw new AuthenticationFailureException("RS256 is required.");
        var kid = GetRequiredString(header.RootElement, "kid");
        if (kid.Length is 0 or > 256) throw new AuthenticationFailureException("Invalid key id.");

        var now = _clock.GetUtcNow();
        if (!_keys.TryGetValue(kid, out var key) || now >= _expiresAt)
        {
            await RefreshAsync(now, cancellationToken);
            _keys.TryGetValue(kid, out key);
        }
        if (key is null) throw new AuthenticationFailureException("Signing key is unavailable.");
        byte[] signature;
        try { signature = Decode(parts[2]); }
        catch (FormatException exception) { throw new AuthenticationFailureException("Invalid signature encoding.", exception); }
        try
        {
            if (!key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new AuthenticationFailureException("Invalid signature.");
        }
        catch (CryptographicException exception) { throw new AuthenticationFailureException("Invalid signature.", exception); }

        using var payload = ParsePart(parts[1]);
        var claims = payload.RootElement;
        if (claims.ValueKind != JsonValueKind.Object || GetRequiredString(claims, "iss") != _issuer.AbsoluteUri.TrimEnd('/')) throw new AuthenticationFailureException("Issuer mismatch.");
        if (!HasAudience(claims, _audience)) throw new AuthenticationFailureException("Audience mismatch.");
        var exp = GetRequiredInteger(claims, "exp");
        var nbf = GetRequiredInteger(claims, "nbf");
        var timestamp = now.ToUnixTimeSeconds();
        if (exp <= timestamp || nbf > timestamp) throw new AuthenticationFailureException("Assertion is outside its validity period.");
        var email = NormalizeEmail(GetRequiredString(claims, "email"));
        if (_owners.Contains(email)) return new(email, "owner");
        if (_viewers.Contains(email)) return new(email, "viewer");
        throw new AuthenticationFailureException("Identity is not allowlisted.");
    }

    private async Task RefreshAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (now - _lastRefreshAttempt < _refreshCooldown) return;
            _lastRefreshAttempt = now;
            IReadOnlyDictionary<string, RSA>? candidate = null;
            try
            {
                var document = await _fetcher.FetchAsync(_jwksUri, cancellationToken);
                candidate = ParseKeys(document);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { return; }
            foreach (var old in _keys.Values) if (!candidate.Values.Contains(old)) old.Dispose();
            _keys = candidate;
            _expiresAt = now + _cacheTtl;
        }
        finally { _refreshLock.Release(); }
    }

    private static IReadOnlyDictionary<string, RSA> ParseKeys(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (!document.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Malformed JWKS.");
        var result = new Dictionary<string, RSA>(StringComparer.Ordinal);
        foreach (var item in keys.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryString(item, "kty", out var kty) || kty != "RSA" || !TryString(item, "kid", out var kid) || kid.Length is 0 or > 256) continue;
            if (TryString(item, "alg", out var alg) && alg != "RS256") continue;
            if (TryString(item, "use", out var use) && use != "sig") continue;
            if (result.ContainsKey(kid)) throw new InvalidDataException("Duplicate key id.");
            if (!TryString(item, "n", out var modulus) || !TryString(item, "e", out var exponent)) continue;
            try
            {
                var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters { Modulus = Decode(modulus), Exponent = Decode(exponent) });
                result.Add(kid, rsa);
            }
            catch (CryptographicException) { }
            catch (FormatException) { }
        }
        if (result.Count == 0) throw new InvalidDataException("No usable signing keys.");
        return result;
    }

    private static JsonDocument ParsePart(string value)
    {
        try { return JsonDocument.Parse(Decode(value), new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException exception) { throw new AuthenticationFailureException("Invalid assertion JSON.", exception); }
        catch (FormatException exception) { throw new AuthenticationFailureException("Invalid assertion encoding.", exception); }
    }

    private static byte[] Decode(string value)
    {
        if (value.Length > 350_000) throw new FormatException("Encoded value is too large.");
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) throw new FormatException("Invalid base64url.");
        var padded = value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private static bool HasAudience(JsonElement claims, string expected)
    {
        if (!claims.TryGetProperty("aud", out var audience)) return false;
        if (audience.ValueKind == JsonValueKind.String) return audience.GetString() == expected;
        if (audience.ValueKind != JsonValueKind.Array) return false;
        var values = audience.EnumerateArray().ToArray();
        return values.All(x => x.ValueKind == JsonValueKind.String) && values.Any(x => x.GetString() == expected);
    }

    private static long GetRequiredInteger(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var result) ? result : throw new AuthenticationFailureException($"Invalid {name} claim.");
    private static string GetRequiredString(JsonElement value, string name) => TryString(value, name, out var result) ? result : throw new AuthenticationFailureException($"Invalid {name} claim.");
    private static bool TryString(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        result = property.GetString() ?? string.Empty;
        return true;
    }

    private static Uri ValidateOrigin(string value, bool cloudflare)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.', StringComparison.Ordinal) || uri.Host.Contains('*', StringComparison.Ordinal)) throw new OptionsValidationException(AccessOptions.Section, typeof(AccessOptions), ["Origins must be exact HTTPS DNS origins."]);
        if (cloudflare && (!uri.Host.EndsWith(".cloudflareaccess.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("cloudflareaccess.com", StringComparison.OrdinalIgnoreCase))) throw new OptionsValidationException(AccessOptions.Section, typeof(AccessOptions), ["Team domain must be Cloudflare Access."]);
        return new Uri($"https://{uri.IdnHost.ToLowerInvariant()}/");
    }

    private static HashSet<string> NormalizeAllowlist(IEnumerable<string> values, string role)
    {
        var result = values.Select(NormalizeEmail).ToHashSet(StringComparer.Ordinal);
        if (result.Count == 0) throw new OptionsValidationException(AccessOptions.Section, typeof(AccessOptions), [$"{role} allowlist is empty."]);
        return result;
    }

    private static string NormalizeEmail(string value)
    {
        var email = value.Trim().ToLowerInvariant();
        if (email.Length is 0 or > 254 || email.Count(x => x == '@') != 1 || email.Any(x => char.IsControl(x) || char.IsWhiteSpace(x))) throw new AuthenticationFailureException("Invalid email.");
        var pieces = email.Split('@');
        if (pieces[0].Length == 0 || pieces[1].Length == 0 || !pieces[1].Contains('.', StringComparison.Ordinal) || pieces[1].StartsWith(".", StringComparison.Ordinal) || pieces[1].EndsWith(".", StringComparison.Ordinal)) throw new AuthenticationFailureException("Invalid email.");
        return email;
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) key.Dispose();
        _refreshLock.Dispose();
    }
}

public sealed class AuthenticationFailureException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class CloudflareAccessHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IAccessAssertionValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CloudflareAccess";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Cf-Access-Jwt-Assertion", out var assertion) || assertion.Count != 1) return AuthenticateResult.NoResult();
        try
        {
            var identity = await validator.ValidateAsync(assertion.ToString(), Context.RequestAborted);
            var claims = new[] { new Claim(ClaimTypes.Email, identity.Email), new Claim(ClaimTypes.Role, identity.Role) };
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
        }
        catch (AuthenticationFailureException exception) { return AuthenticateResult.Fail(exception); }
    }
}
