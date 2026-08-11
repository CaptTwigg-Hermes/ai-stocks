using System.Net;
using System.Net.Http.Json;
using AiStocks.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AiStocks.Api.Tests;

public sealed class ProductionAccessBoundaryTests
{
    [Fact]
    public void Production_rejects_invalid_access_configuration_before_health_can_report_ready()
    {
        using var factory = new InvalidProductionApiFactory();
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Production_requires_valid_access_identity_and_does_not_expose_preview_routes()
    {
        await using var factory = new ProductionApiFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync("/api/v1/portfolio")).StatusCode);

        using var forged = factory.CreateClient();
        forged.DefaultRequestHeaders.Add("Cf-Access-Jwt-Assertion", "forged");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await forged.GetAsync("/api/v1/me")).StatusCode);

        using var viewer = factory.CreateClient();
        viewer.DefaultRequestHeaders.Add("Cf-Access-Jwt-Assertion", "viewer-token");
        Assert.Equal(HttpStatusCode.OK,
            (await viewer.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync("/api/v1/portfolio")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.PostAsJsonAsync("/api/v1/orders", Order())).StatusCode);

        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Add("Cf-Access-Jwt-Assertion", "owner-token");
        Assert.Equal(HttpStatusCode.OK,
            (await owner.GetAsync("/api/v1/me")).StatusCode);
    }

    private static object Order() => new { side = "buy", instrumentId = "aapl-us", quantity = 1 };
}

internal sealed class InvalidProductionApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("UI_ORIGINS", "https://stocks.example.com");
        builder.UseSetting("ACCESS_TEAM_DOMAIN", "https://contest.cloudflareaccess.com");
        builder.UseSetting("ACCESS_AUD", "production-audience");
        builder.UseSetting("API_PUBLIC_ORIGIN", "https://api.stocks.example.com");
        builder.UseSetting("ACCESS_OWNER_EMAILS", "same@example.com");
        builder.UseSetting("ACCESS_VIEWER_EMAILS", "same@example.com");
    }
}

internal sealed class ProductionApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("UI_ORIGINS", "https://stocks.example.com");
        builder.UseSetting("ACCESS_TEAM_DOMAIN", "https://contest.cloudflareaccess.com");
        builder.UseSetting("ACCESS_AUD", "production-audience");
        builder.UseSetting("API_PUBLIC_ORIGIN", "https://api.stocks.example.com");
        builder.UseSetting("ACCESS_OWNER_EMAILS", "owner@example.com");
        builder.UseSetting("ACCESS_VIEWER_EMAILS", "viewer@example.com");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccessAssertionValidator>();
            services.AddSingleton<IAccessAssertionValidator, ProductionTestAccessValidator>();
        });
    }
}

internal sealed class ProductionTestAccessValidator : IAccessAssertionValidator
{
    public Task<AccessIdentity> ValidateAsync(string assertion, CancellationToken cancellationToken) => assertion switch
    {
        "owner-token" => Task.FromResult(new AccessIdentity("owner@example.com", "owner")),
        "viewer-token" => Task.FromResult(new AccessIdentity("viewer@example.com", "viewer")),
        _ => Task.FromException<AccessIdentity>(new AuthenticationFailureException("invalid"))
    };
}
