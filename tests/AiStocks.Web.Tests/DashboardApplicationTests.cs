using System.Net;
using System.Net.Http.Json;
using AiStocks.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AiStocks.Web.Tests;

public sealed class DashboardApplicationTests : IClassFixture<DashboardApplicationFactory>
{
    private readonly DashboardApplicationFactory _factory;

    public DashboardApplicationTests(DashboardApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Private_routes_reject_missing_identity()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Global_ip_limiter_rejects_before_expensive_authentication()
    {
        await using var factory = new DashboardApplicationFactory("192.0.2.1");
        for (var index = 0; index < 100; index++)
        {
            var context = await SendFromLoopback(factory, $"198.51.100.{index + 1}");
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        }

        var rejected = await SendFromLoopback(factory, "198.51.100.254");
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Response.StatusCode);
        Assert.Equal(100, factory.Validator.Calls);
    }

    [Fact]
    public async Task Global_limiter_uses_forwarded_client_only_from_configured_tunnel_proxy()
    {
        await using var factory = new DashboardApplicationFactory("127.0.0.1");
        for (var index = 0; index < 100; index++)
        {
            var context = await SendFromLoopback(factory, $"203.0.113.{index + 1}");
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        }
        Assert.Equal(100, factory.Validator.Calls);
    }

    private static Task<HttpContext> SendFromLoopback(DashboardApplicationFactory factory, string forwardedFor) =>
        factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = HttpMethod.Get.Method;
            context.Request.Path = "/api/dashboard";
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            context.Request.Headers["Cf-Access-Jwt-Assertion"] = "malformed-token";
        });

    [Fact]
    public async Task Trusted_tunnel_proxy_restores_https_scheme_for_secure_antiforgery_cookie()
    {
        await using var factory = new DashboardApplicationFactory("127.0.0.1");
        var context = await factory.Server.SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Loopback;
            request.Request.Method = HttpMethod.Get.Method;
            request.Request.Path = "/";
            request.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
            request.Request.Headers["X-Forwarded-Proto"] = "https";
            request.Request.Headers["Cf-Access-Jwt-Assertion"] = "viewer-token";
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(context.Response.Headers.SetCookie, value => value is not null && value.Contains("__Host-AiStocks-Csrf", StringComparison.Ordinal) && value.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dashboard_renders_every_required_mobile_section_and_security_headers()
    {
        using var client = ViewerClient();
        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var heading in new[] { "Leaderboard", "Performance chart", "Portfolios", "Queued orders", "Evidence timeline", "Fees", "Dividends", "Failures", "Audit history" })
            Assert.Contains(heading, html, StringComparison.Ordinal);
        Assert.Contains("width=device-width", html, StringComparison.Ordinal);
        Assert.Contains("/assets/dashboard.css", html, StringComparison.Ordinal);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("Owner controls", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_json_routes_are_available_to_viewers_and_unknown_mutations_are_absent()
    {
        using var client = ViewerClient();
        foreach (var path in new[] { "dashboard", "leaderboard", "portfolios", "queued-orders", "evidence", "fees", "dividends", "failures", "audit" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/{path}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/trades", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsJsonAsync("/api/portfolios", new { })).StatusCode);
    }

    [Fact]
    public async Task Owner_controls_require_owner_origin_antiforgery_and_idempotency()
    {
        using var viewer = ViewerClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/admin/pause", null)).StatusCode);

        using var owner = OwnerClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/admin/pause", null)).StatusCode);

        var page = await owner.GetStringAsync("/");
        var token = Extract(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"");
        using (var wrongOrigin = ControlRequest(token, "https://evil.example", "wrong-origin"))
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.SendAsync(wrongOrigin)).StatusCode);
        using (var missingKey = ControlRequest(token, "https://stocks.example.com", null))
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(missingKey)).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/pause");
        request.Headers.Add("Origin", "https://stocks.example.com");
        request.Headers.Add("Idempotency-Key", "pause-2026-08-08");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
        using var response = await owner.SendAsync(request);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains(_factory.Facade.Commands, x => x.Action == ContestControlAction.Pause && x.IdempotencyKey == "pause-2026-08-08");
    }

    [Fact]
    public async Task Facade_failure_is_fail_closed_not_a_partially_rendered_success()
    {
        _factory.Facade.ThrowOnQuery = true;
        try
        {
            using var client = ViewerClient();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/dashboard")).StatusCode);
        }
        finally { _factory.Facade.ThrowOnQuery = false; }
    }

    private HttpClient ViewerClient() => AuthenticatedClient("viewer@example.com", "viewer");
    private HttpClient OwnerClient() => AuthenticatedClient("owner@example.com", "owner");

    private HttpClient AuthenticatedClient(string email, string role)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        client.BaseAddress = new Uri("https://localhost");
        client.DefaultRequestHeaders.Add("Cf-Access-Jwt-Assertion", role + "-token");
        return client;
    }

    private static HttpRequestMessage ControlRequest(string token, string origin, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/pause");
        request.Headers.Add("Origin", origin);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
        return request;
    }

    private static string Extract(string value, string start, string end)
    {
        var offset = value.IndexOf(start, StringComparison.Ordinal) + start.Length;
        return value[offset..value.IndexOf(end, offset, StringComparison.Ordinal)];
    }
}

public sealed class DashboardApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string? trustedProxy;
    public DashboardApplicationFactory() { }
    internal DashboardApplicationFactory(string trustedProxy) => this.trustedProxy = trustedProxy;
    public RecordingFacade Facade { get; } = new();
    public TestAccessAssertionValidator Validator { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (trustedProxy is not null) builder.UseSetting("TRUSTED_PROXY_IPS", trustedProxy);
        builder.ConfigureTestServices(services =>
        {
            if (trustedProxy is not null)
                services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
                {
                    options.KnownIPNetworks.Clear();
                    options.KnownProxies.Clear();
                    options.KnownProxies.Add(IPAddress.Parse(trustedProxy));
                });
            services.AddSingleton<IDashboardFacade>(Facade);
            services.AddSingleton<IAccessAssertionValidator>(Validator);
        });
    }
}

public sealed class TestAccessAssertionValidator : IAccessAssertionValidator
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    public Task<AccessIdentity> ValidateAsync(string assertion, CancellationToken cancellationToken) => assertion switch
    {
        "owner-token" => Count(Task.FromResult(new AccessIdentity("owner@example.com", "owner"))),
        "viewer-token" => Count(Task.FromResult(new AccessIdentity("viewer@example.com", "viewer"))),
        _ => Count(Task.FromException<AccessIdentity>(new AuthenticationFailureException("invalid")))
    };

    private Task<AccessIdentity> Count(Task<AccessIdentity> result)
    {
        Interlocked.Increment(ref _calls);
        return result;
    }
}

public sealed class RecordingFacade : IDashboardFacade
{
    public bool ThrowOnQuery { get; set; }
    public List<ContestControlCommand> Commands { get; } = [];

    public Task<DashboardSnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        if (ThrowOnQuery) throw new DashboardUnavailableException("offline");
        return Task.FromResult(DashboardFixtures.Snapshot);
    }

    public Task<ContestControlResult> ControlAsync(ContestControlCommand command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return Task.FromResult(new ContestControlResult("paused", true));
    }
}