using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiStocks.Api.Tests;

public sealed class ApiEndpointInventoryTests : IClassFixture<ApiApplicationFactory>
{
    private readonly ApiApplicationFactory _factory;

    public ApiEndpointInventoryTests(ApiApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Healthz_returns_json_ready_status()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/healthz");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Me_requires_identity_and_returns_authenticated_user_as_json()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/me")).StatusCode);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Email", "Owner@Example.COM");
        client.DefaultRequestHeaders.Add("X-Test-User-Role", "owner");

        using var response = await client.GetAsync("/api/v1/me");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("owner@example.com", body.RootElement.GetProperty("email").GetString());
        Assert.Equal("owner", body.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Endpoint_inventory_contains_only_the_functional_preview_surface()
    {
        using var client = _factory.CreateClient();

        var inventory = await client.GetFromJsonAsync<EndpointInventoryItem[]>("/__endpoint-inventory");
        var expected = new[]
        {
            new EndpointInventoryItem("GET", "/api/v1/instruments"),
            new EndpointInventoryItem("GET", "/api/v1/leaderboard"),
            new EndpointInventoryItem("GET", "/api/v1/me"),
            new EndpointInventoryItem("GET", "/api/v1/orders"),
            new EndpointInventoryItem("POST", "/api/v1/orders"),
            new EndpointInventoryItem("GET", "/api/v1/portfolio"),
            new EndpointInventoryItem("GET", "/healthz")
        };

        Assert.Equal(
            expected,
            inventory?.OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal).ThenBy(endpoint => endpoint.Method, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(inventory ?? [], endpoint => endpoint.Path.Contains("broker", StringComparison.OrdinalIgnoreCase) || endpoint.Path.Contains("live-order", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Api_does_not_serve_html_root()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Cors_allows_only_configured_ui_origin()
    {
        using var client = _factory.CreateClient();
        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/v1/me");
        allowed.Headers.Add("Origin", "https://stocks.example.com");
        allowed.Headers.Add("Access-Control-Request-Method", "GET");

        using var allowedResponse = await client.SendAsync(allowed);

        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);
        Assert.Equal("https://stocks.example.com", allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var hostile = new HttpRequestMessage(HttpMethod.Options, "/api/v1/me");
        hostile.Headers.Add("Origin", "https://evil.example");
        hostile.Headers.Add("Access-Control-Request-Method", "GET");

        using var hostileResponse = await client.SendAsync(hostile);

        Assert.False(hostileResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private sealed record EndpointInventoryItem(string Method, string Path);
}

public sealed class ApiApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
}

public sealed class AiExhibitionEndpointTests
{
    [Fact]
    public async Task Exhibition_inventory_exposes_only_ai_fixture_surface()
    {
        await using var factory = new AiExhibitionApiFactory();
        using var client = factory.CreateClient();

        var inventory = await client.GetFromJsonAsync<EndpointInventoryItem[]>("/__endpoint-inventory");

        Assert.Contains(inventory ?? [], endpoint => endpoint == new EndpointInventoryItem("GET", "/api/v1/ai-progress"));
        Assert.Contains(inventory ?? [], endpoint => endpoint == new EndpointInventoryItem("POST", "/internal/preview/ai-decisions"));
        Assert.DoesNotContain(inventory ?? [], endpoint => endpoint.Path is "/api/v1/portfolio" or "/api/v1/orders");
    }

    private sealed record EndpointInventoryItem(string Method, string Path);
}

internal sealed class AiExhibitionApiFactory : WebApplicationFactory<Program>
{
    public const string Secret = "0123456789abcdef0123456789abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("PREVIEW_MODE", "1");
        builder.UseSetting("AI_EXHIBITION_MODE", "1");
        builder.UseSetting("AI_EXHIBITION_KEY", Secret);
    }
}
