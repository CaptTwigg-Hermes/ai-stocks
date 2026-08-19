using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using AiStocks.Api;
using AiStocks.MarketData;
using AiStocks.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
});

var previewMode = builder.Configuration["PREVIEW_MODE"] == "1";
var aiExhibitionMode = builder.Configuration["AI_EXHIBITION_MODE"] == "1";
var localAuth = builder.Environment.IsEnvironment("Testing") || (builder.Environment.IsDevelopment() && previewMode);
if (previewMode && !localAuth)
    throw new InvalidOperationException("PREVIEW_MODE is permitted only in Development or Testing.");
if (aiExhibitionMode && (!previewMode || !localAuth))
    throw new InvalidOperationException("AI_EXHIBITION_MODE requires PREVIEW_MODE in Development or Testing.");
var aiExhibitionKey = aiExhibitionMode ? Required("AI_EXHIBITION_KEY") : null;
var aiExhibitionArchivePath = aiExhibitionMode ? Required("AI_EXHIBITION_ARCHIVE_PATH") : null;
if (aiExhibitionKey is not null && aiExhibitionKey.Length < 32)
    throw new InvalidOperationException("AI_EXHIBITION_KEY must contain at least 32 characters.");

var uiOrigins = (builder.Configuration["UI_ORIGINS"] ?? (localAuth
        ? "http://192.168.50.2:3232,https://stocks.example.com"
        : Required("UI_ORIGINS")))
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(NormalizeOrigin)
    .Where(origin => origin is not null)
    .Select(origin => origin!)
    .Distinct(StringComparer.Ordinal)
    .ToArray();
if (uiOrigins.Length == 0 || (!localAuth && uiOrigins.Any(origin => !origin.StartsWith("https://", StringComparison.Ordinal))))
    throw new InvalidOperationException("UI_ORIGINS must contain exact HTTPS origins (HTTP is local-preview only).");

builder.Services.AddCors(options =>
{
    options.AddPolicy("Ui", policy => policy
        .WithOrigins(uiOrigins)
        .WithMethods("GET", "POST")
        .WithHeaders("Content-Type", "Idempotency-Key")
        .AllowCredentials());
});

if (localAuth)
{
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = LocalPreviewAccessHandler.SchemeName;
        options.DefaultChallengeScheme = LocalPreviewAccessHandler.SchemeName;
    }).AddScheme<AuthenticationSchemeOptions, LocalPreviewAccessHandler>(LocalPreviewAccessHandler.SchemeName, _ => { });
}
else
{
    builder.Services.Configure<AccessOptions>(options =>
    {
        options.TeamDomain = Required("ACCESS_TEAM_DOMAIN");
        options.Audience = Required("ACCESS_AUD");
        options.PublicOrigin = Required("API_PUBLIC_ORIGIN");
        options.OwnerEmails = Emails("ACCESS_OWNER_EMAILS");
        options.ViewerEmails = Emails("ACCESS_VIEWER_EMAILS");
    });
    builder.Services.AddHttpClient<IJwksFetcher, BoundedJwksFetcher>(client => client.Timeout = TimeSpan.FromSeconds(5))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None
        });
    builder.Services.AddSingleton<IAccessAssertionValidator, CloudflareAccessValidator>();
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = CloudflareAccessHandler.SchemeName;
        options.DefaultChallengeScheme = CloudflareAccessHandler.SchemeName;
    }).AddScheme<AuthenticationSchemeOptions, CloudflareAccessHandler>(CloudflareAccessHandler.SchemeName, _ => { });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Trade", policy => policy.RequireAuthenticatedUser().RequireRole("owner"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("orders", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.Email) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddSingleton(TimeProvider.System);
if (localAuth) builder.Services.AddSingleton<PreviewRaceStore>();
if (aiExhibitionArchivePath is not null)
    builder.Services.AddSingleton(services => new DelayedNasdaqInstrumentStore(
        aiExhibitionArchivePath, services.GetRequiredService<TimeProvider>()));

var app = builder.Build();
if (!localAuth) _ = app.Services.GetRequiredService<IAccessAssertionValidator>();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseCors("Ui");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new HealthResponse("ready", localAuth ? "preview" : "access",
    aiExhibitionMode ? DelayedNasdaqInstrumentStore.DataMode : localAuth ? PreviewRaceStore.DataMode : "disabled")));

var api = app.MapGroup("/api/v1").RequireAuthorization();
api.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new IdentityDto(Identity(user), Role(user))));
if (localAuth)
{
    if (aiExhibitionMode)
        api.MapGet("/instruments", (string? query, DelayedNasdaqInstrumentStore store) => Results.Ok(store.Search(query)));
    else
        api.MapGet("/instruments", (string? query, PreviewRaceStore store) => Results.Ok(store.Search(query)));
    if (aiExhibitionMode)
        api.MapGet("/leaderboard", (PreviewRaceStore store, DelayedNasdaqInstrumentStore market) =>
        {
            var snapshot = market.Search(null);
            return Results.Ok(store.AiLeaderboard(snapshot));
        });
    else
        api.MapGet("/leaderboard", (ClaimsPrincipal user, PreviewRaceStore store) =>
            Results.Ok(store.Leaderboard(Identity(user))));
    if (aiExhibitionMode)
    {
        api.MapGet("/ai-progress", (PreviewRaceStore store, DelayedNasdaqInstrumentStore market) =>
        {
            var snapshot = market.Search(null);
            return Results.Ok(store.AiProgress(snapshot));
        });
        app.MapPost("/internal/preview/ai-status", (AiStatusRequestDto request, PreviewRaceStore store, HttpContext context) =>
        {
            if (!ValidSecret(context, aiExhibitionKey!)) return Results.Unauthorized();
            try
            {
                store.UpdateAiStatus(request);
                return Results.Ok();
            }
            catch (PreviewOrderException exception)
            {
                return ApiEndpointResults.Problem(exception.Code, "AI fixture status rejected",
                    StatusCodes.Status400BadRequest, context, exception.Message);
            }
        }).WithMetadata(new Microsoft.AspNetCore.Cors.DisableCorsAttribute());
        app.MapPost("/internal/preview/ai-decisions", (AiDecisionRequestDto request, PreviewRaceStore store,
            DelayedNasdaqInstrumentStore market, HttpContext context) =>
        {
            if (!ValidSecret(context, aiExhibitionKey!)) return Results.Unauthorized();
            try
            {
                var snapshot = market.Search(null);
                var submission = store.SubmitAi(request, snapshot);
                return submission.Replayed ? Results.Ok(submission.Decision) : Results.Created("/api/v1/ai-progress", submission.Decision);
            }
            catch (PreviewOrderException exception)
            {
                return ApiEndpointResults.Problem(exception.Code, "AI fixture decision rejected",
                    StatusCodes.Status400BadRequest, context, exception.Message);
            }
        }).WithMetadata(new Microsoft.AspNetCore.Cors.DisableCorsAttribute());
    }
    else
    {
        api.MapGet("/portfolio", (ClaimsPrincipal user, PreviewRaceStore store) => Results.Ok(store.Portfolio(Identity(user))));
        api.MapGet("/orders", (ClaimsPrincipal user, PreviewRaceStore store) => Results.Ok(store.Orders(Identity(user))));
        api.MapPost("/orders", (ClaimsPrincipal user, HumanOrderRequestDto request, PreviewRaceStore store, HttpContext context) =>
        {
            if (!builder.Environment.IsEnvironment("Testing") && !ExactOrigin(context, uiOrigins))
                return ApiEndpointResults.Problem("origin-rejected", "Origin rejected", StatusCodes.Status403Forbidden, context,
                    "Paper orders require an exact configured UI origin.");
            if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1)
                return ApiEndpointResults.Problem("idempotency-key-required", "Idempotency key required",
                    StatusCodes.Status400BadRequest, context, "Send exactly one Idempotency-Key header.");
            try
            {
                var submission = store.Submit(Identity(user), keys.ToString(), request);
                context.Response.Headers["Idempotency-Replayed"] = submission.Replayed ? "true" : "false";
                return submission.Replayed
                    ? Results.Ok(submission.Order)
                    : Results.Created($"/api/v1/orders/{submission.Order.Id}", submission.Order);
            }
            catch (PreviewOrderException exception)
            {
                return ApiEndpointResults.Problem(exception.Code, "Paper order rejected",
                    StatusCodes.Status400BadRequest, context, exception.Message);
            }
        }).RequireAuthorization("Trade").RequireRateLimiting("orders");
    }
}

if (app.Environment.IsEnvironment("Testing"))
    app.MapGet("/__endpoint-inventory", () => Results.Ok(EndpointInventory(app)));

if (args.Contains("--print-endpoints", StringComparer.Ordinal))
{
    var endpoints = ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(source => source.Endpoints)
        .OfType<RouteEndpoint>()
        .SelectMany(endpoint =>
            (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
            .Select(method => new
            {
                method,
                path = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/')
            }))
        .OrderBy(endpoint => endpoint.path, StringComparer.Ordinal)
        .ThenBy(endpoint => endpoint.method, StringComparer.Ordinal);
    Console.WriteLine("AISTOCKS_ENDPOINTS=" + JsonSerializer.Serialize(endpoints));
    return;
}

app.Run();

string Required(string name)
{
    var value = builder.Configuration[name];
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
    return value;
}
string[] Emails(string name) => Required(name).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

static string? NormalizeOrigin(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/"
        || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
    return uri.GetLeftPart(UriPartial.Authority);
}
static bool ExactOrigin(HttpContext context, IReadOnlyCollection<string> allowed)
{
    if (!context.Request.Headers.TryGetValue("Origin", out var origins) || origins.Count != 1) return false;
    return allowed.Contains(origins.ToString(), StringComparer.Ordinal);
}
static bool ValidSecret(HttpContext context, string expected)
{
    if (!context.Request.Headers.TryGetValue("X-AI-Exhibition-Key", out var values) || values.Count != 1) return false;
    var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(values.ToString()));
    var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
}
static string Identity(ClaimsPrincipal user) =>
    user.FindFirstValue(ClaimTypes.Email) ?? throw new InvalidOperationException("Authenticated identity lacks email.");
static string Role(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Role) ?? "viewer";
static EndpointInventoryItem[] EndpointInventory(WebApplication app) => ((IEndpointRouteBuilder)app).DataSources
    .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
    .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/__", StringComparison.Ordinal) != true)
    .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
        .Select(method => new EndpointInventoryItem(method, "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'))))
    .OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal).ThenBy(endpoint => endpoint.Method, StringComparer.Ordinal).ToArray();

internal sealed record HealthResponse(string Status, string Mode, string DataMode);
internal sealed record EndpointInventoryItem(string Method, string Path);

internal sealed class LocalPreviewAccessHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalPreviewAccess";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (environment.IsEnvironment("Testing"))
        {
            if (!Request.Headers.TryGetValue("X-Test-User-Email", out var emails) || emails.Count != 1)
                return Task.FromResult(AuthenticateResult.NoResult());
            var email = emails.ToString().Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email)) return Task.FromResult(AuthenticateResult.NoResult());
            var role = Request.Headers.TryGetValue("X-Test-User-Role", out var roles) && roles.Count == 1
                ? roles.ToString().Trim().ToLowerInvariant() : "viewer";
            return Task.FromResult(Success(email, role));
        }
        return Task.FromResult(Success("preview@local", "owner"));
    }

    private static AuthenticateResult Success(string email, string role)
    {
        var claims = new[] { new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Role, role) };
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }
}

public partial class Program;
