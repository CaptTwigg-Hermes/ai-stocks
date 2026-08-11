using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiStocks.Security;
using Microsoft.Extensions.Options;

namespace AiStocks.Web.Tests;

public sealed class CloudflareAccessValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Valid_rs256_assertion_requires_exact_claims_and_assigns_local_role()
    {
        using var key = RSA.Create(2048);
        var fetcher = new FakeJwksFetcher(Jwks(key, "key"));
        using var validator = Validator(fetcher);

        var viewer = await validator.ValidateAsync(Token(key, "key"), default);
        var owner = await validator.ValidateAsync(Token(key, "key", email: "OWNER@example.com"), default);

        Assert.Equal(new AccessIdentity("viewer@example.com", "viewer"), viewer);
        Assert.Equal(new AccessIdentity("owner@example.com", "owner"), owner);
        Assert.Equal(1, fetcher.Calls);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("email")]
    [InlineData("algorithm")]
    [InlineData("kid")]
    public async Task Invalid_algorithm_claims_kid_or_membership_fail_closed(string fault)
    {
        using var key = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var fetcher = new FakeJwksFetcher(Jwks(key, "key"));
        using var validator = Validator(fetcher);
        var token = fault switch
        {
            "issuer" => Token(key, "key", issuer: "https://other.cloudflareaccess.com"),
            "audience" => Token(key, "key", audience: "wrong"),
            "expired" => Token(key, "key", exp: Now.ToUnixTimeSeconds()),
            "future" => Token(key, "key", nbf: Now.AddSeconds(1).ToUnixTimeSeconds()),
            "email" => Token(key, "key", email: "stranger@example.com"),
            "algorithm" => Token(key, "key", algorithm: "RS512"),
            "kid" => Token(other, "missing"),
            _ => throw new InvalidOperationException()
        };
        await Assert.ThrowsAsync<AuthenticationFailureException>(() => validator.ValidateAsync(token, default));
    }

    [Fact]
    public async Task Failed_refresh_is_throttled_and_last_known_good_survives_malformed_jwks()
    {
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        var clock = new MutableTimeProvider(Now);
        var fetcher = new FakeJwksFetcher(Jwks(first, "first"));
        using var validator = Validator(fetcher, clock, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Assert.Equal("viewer", (await validator.ValidateAsync(Token(first, "first"), default)).Role);

        fetcher.Document = Encoding.UTF8.GetBytes("{\"keys\":[]}");
        clock.Now = clock.Now.AddSeconds(2);
        Assert.Equal("viewer", (await validator.ValidateAsync(Token(first, "first"), default)).Role);
        var callsAfterFailure = fetcher.Calls;
        await Assert.ThrowsAsync<AuthenticationFailureException>(() => validator.ValidateAsync(Token(second, "second"), default));
        Assert.Equal(callsAfterFailure, fetcher.Calls);

        fetcher.Document = Jwks(second, "second");
        clock.Now = clock.Now.AddSeconds(2);
        Assert.Equal("viewer", (await validator.ValidateAsync(Token(second, "second"), default)).Role);
    }

    [Fact]
    public async Task Concurrent_rotation_never_disposes_a_key_visible_to_an_active_validator()
    {
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        var clock = new MutableTimeProvider(Now);
        var fetcher = new FakeJwksFetcher(Jwks(first, "first"));
        using var validator = Validator(fetcher, clock, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));
        var firstToken = Token(first, "first");
        Assert.Equal("viewer", (await validator.ValidateAsync(firstToken, default)).Role);

        fetcher.Document = Jwks(second, "second");
        clock.Now = clock.Now.AddSeconds(2);
        var validations = Enumerable.Range(0, 2_000)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await validator.ValidateAsync(firstToken, default);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }))
            .ToArray();
        var rotation = validator.ValidateAsync(Token(second, "second"), default);

        var errors = await Task.WhenAll(validations);
        var rotatedIdentity = await rotation;
        Assert.DoesNotContain(errors, error => error is not null and not AuthenticationFailureException);
        Assert.Contains(errors, error => error is null);
        Assert.Equal("viewer", rotatedIdentity.Role);
    }

    private static CloudflareAccessValidator Validator(FakeJwksFetcher fetcher, TimeProvider? clock = null, TimeSpan? ttl = null, TimeSpan? cooldown = null) => new(
        Options.Create(new AccessOptions
        {
            TeamDomain = "https://contest.cloudflareaccess.com",
            Audience = "aud",
            PublicOrigin = "https://stocks.example.com",
            OwnerEmails = ["owner@example.com"],
            ViewerEmails = ["viewer@example.com"],
            JwksCacheTtl = ttl ?? TimeSpan.FromMinutes(5),
            RefreshCooldown = cooldown ?? TimeSpan.FromSeconds(30)
        }), fetcher, clock ?? new MutableTimeProvider(Now));

    private static byte[] Jwks(RSA key, string kid)
    {
        var parameters = key.ExportParameters(false);
        return JsonSerializer.SerializeToUtf8Bytes(new { keys = new[] { new { kty = "RSA", kid, alg = "RS256", use = "sig", n = Encode(parameters.Modulus!), e = Encode(parameters.Exponent!) } } });
    }

    private static string Token(RSA key, string kid, string issuer = "https://contest.cloudflareaccess.com", string audience = "aud", long? exp = null, long? nbf = null, string email = "viewer@example.com", string algorithm = "RS256")
    {
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = algorithm, kid }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = issuer, aud = audience, exp = exp ?? Now.AddHours(1).ToUnixTimeSeconds(), nbf = nbf ?? Now.AddMinutes(-1).ToUnixTimeSeconds(), email }));
        var input = Encoding.ASCII.GetBytes($"{header}.{payload}");
        var signature = key.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Encode(signature)}";
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class FakeJwksFetcher(byte[] document) : IJwksFetcher
    {
        public byte[] Document { get; set; } = document;
        public int Calls { get; private set; }
        public Task<byte[]> FetchAsync(Uri uri, CancellationToken cancellationToken) { Calls++; return Task.FromResult(Document); }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
