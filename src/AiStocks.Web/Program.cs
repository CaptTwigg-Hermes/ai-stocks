using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using AiStocks.Persistence;
using AiStocks.Security;
using AiStocks.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using AiStocks.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.UseAiStocksSerilog("AiStocks.Web");
if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.Configure<AccessOptions>(builder.Configuration.GetSection(AccessOptions.Section));
    builder.Services.AddSingleton<IDashboardFacade, FailClosedDashboardFacade>();
}
else
{
    builder.Services.Configure<AccessOptions>(options =>
    {
        options.TeamDomain = Required("ACCESS_TEAM_DOMAIN");
        options.Audience = Required("ACCESS_AUD");
        options.PublicOrigin = Required("PUBLIC_ORIGIN");
        options.OwnerEmails = Emails("ACCESS_OWNER_EMAILS");
        options.ViewerEmails = Emails("ACCESS_VIEWER_EMAILS");
    });
    var dataSource = NpgsqlDataSource.Create(PostgresConfiguration.Require(PostgresConfiguration.Environment(), "DATABASE_URL"));
    builder.Services.AddSingleton(dataSource);
    builder.Services.AddSingleton<IDashboardFacade, PostgresDashboardFacade>();
}
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<IJwksFetcher, BoundedJwksFetcher>(client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None });
builder.Services.AddSingleton<IAccessAssertionValidator, CloudflareAccessValidator>();
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = CloudflareAccessHandler.SchemeName;
    options.DefaultChallengeScheme = CloudflareAccessHandler.SchemeName;
}).AddScheme<AuthenticationSchemeOptions, CloudflareAccessHandler>(CloudflareAccessHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options => options.AddPolicy("Owner", policy => policy.RequireAuthenticatedUser().RequireRole("owner")));
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-AiStocks-Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
var trustedProxyText = builder.Configuration["TRUSTED_PROXY_IPS"];
if (!builder.Environment.IsEnvironment("Testing") && string.IsNullOrWhiteSpace(trustedProxyText))
    throw new InvalidOperationException("TRUSTED_PROXY_IPS is required behind Cloudflare Tunnel.");
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var value in (trustedProxyText ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        if (!IPAddress.TryParse(value, out var address))
            throw new InvalidOperationException("TRUSTED_PROXY_IPS must contain only exact IP addresses.");
        options.KnownProxies.Add(address);
    }
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("queries", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.Email) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("controls", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.Email) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});

var app = builder.Build();
app.UseAiStocksRequestLogging();
if (!builder.Environment.IsEnvironment("Testing"))
    _ = app.Services.GetRequiredService<IAccessAssertionValidator>();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'none'; style-src 'self'; img-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseStaticFiles();
app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", async (IDashboardFacade facade, CancellationToken cancellationToken) =>
{
    try
    {
        _ = await facade.QueryAsync(cancellationToken);
        return Results.Ok(new { status = "ready" });
    }
    catch (DashboardUnavailableException)
    {
        return Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/", async (HttpContext context, IDashboardFacade facade, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    try
    {
        var data = await facade.QueryAsync(cancellationToken);
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Content(DashboardRenderer.Render(data, context.User, tokens), "text/html; charset=utf-8");
    }
    catch (DashboardUnavailableException) { return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Dashboard unavailable"); }
}).RequireAuthorization().RequireRateLimiting("queries");

var api = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("queries");
api.MapGet("/dashboard", Query(snapshot => snapshot));
api.MapGet("/leaderboard", Query(snapshot => snapshot.Leaderboard));
api.MapGet("/portfolios", Query(snapshot => snapshot.Portfolios));
api.MapGet("/queued-orders", Query(snapshot => snapshot.QueuedOrders));
api.MapGet("/evidence", Query(snapshot => snapshot.Evidence));
api.MapGet("/fees", Query(snapshot => snapshot.Fees));
api.MapGet("/dividends", Query(snapshot => snapshot.Dividends));
api.MapGet("/failures", Query(snapshot => snapshot.Failures));
api.MapGet("/audit", Query(snapshot => snapshot.Audit));

var admin = app.MapGroup("/admin").RequireAuthorization("Owner").RequireRateLimiting("controls");
MapControl(admin, "/start", ContestControlAction.Start);
MapControl(admin, "/pause", ContestControlAction.Pause);
MapControl(admin, "/resume", ContestControlAction.Resume);
MapControl(admin, "/pre-start-reset", ContestControlAction.PreStartReset);

if (args.Contains("--print-endpoints", StringComparer.Ordinal))
{
    PrintEndpoints(app);
    return;
}
app.Run();

static Delegate Query(Func<DashboardSnapshot, object> select) => async (IDashboardFacade facade, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(select(await facade.QueryAsync(cancellationToken))); }
    catch (DashboardUnavailableException) { return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Dashboard unavailable"); }
};

string Required(string name)
{
    var value = builder.Configuration[name];
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
    return value;
}

string[] Emails(string name) => Required(name).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

static void MapControl(RouteGroupBuilder group, string pattern, ContestControlAction action)
{
    group.MapPost(pattern, async (HttpContext context, IDashboardFacade facade, IAntiforgery antiforgery, IOptions<AccessOptions> options, CancellationToken cancellationToken) =>
    {
        var expectedOrigin = options.Value.PublicOrigin.TrimEnd('/');
        context.Request.Headers.TryGetValue("Origin", out var origins);
        var suppliedOrigin = origins.ToString();
        var exactOrigin = origins.Count == 1 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(suppliedOrigin), System.Text.Encoding.UTF8.GetBytes(expectedOrigin));
        var opaqueSameOriginNavigation = origins.Count == 1 && suppliedOrigin == "null"
            && context.Request.Headers["Sec-Fetch-Site"] == "same-origin"
            && context.Request.Headers["Sec-Fetch-Mode"] == "navigate"
            && context.Request.Headers["Sec-Fetch-Dest"] == "document"
            && $"{context.Request.Scheme}://{context.Request.Host}" == expectedOrigin;
        if (!exactOrigin && !opaqueSameOriginNavigation)
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Invalid origin");
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Invalid antiforgery token"); }
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key)) key = form["idempotencyKey"].ToString();
        if (key.Length is 0 or > 128 || key.Any(character => character < 0x21 || character > 0x7e))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A printable Idempotency-Key of at most 128 characters is required");
        var email = context.User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email)) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Owner email is required");
        try
        {
            var result = await facade.ControlAsync(new(action, email, key), cancellationToken);
            if (context.Request.HasFormContentType) return Results.Redirect("/", false, false);
            return Results.Ok(result);
        }
        catch (ContestControlRejectedException exception) { return Results.Conflict(new { error = exception.Message }); }
    });
}

static void PrintEndpoints(WebApplication app)
{
    var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
        .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
            .Select(method => new { method, path = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/') }))
        .OrderBy(endpoint => endpoint.path, StringComparer.Ordinal).ThenBy(endpoint => endpoint.method, StringComparer.Ordinal);
    Console.WriteLine("AISTOCKS_ENDPOINTS=" + System.Text.Json.JsonSerializer.Serialize(endpoints));
}

public partial class Program;
