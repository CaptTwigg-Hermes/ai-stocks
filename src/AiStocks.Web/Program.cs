using System.Security.Claims;
using System.Threading.RateLimiting;
using AiStocks.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<AccessOptions>(builder.Configuration.GetSection(AccessOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<IJwksFetcher, BoundedJwksFetcher>(client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None });
builder.Services.AddSingleton<IAccessAssertionValidator, CloudflareAccessValidator>();
builder.Services.AddSingleton<IDashboardFacade, FailClosedDashboardFacade>();
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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("queries", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.Email) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("controls", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.Email) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});

var app = builder.Build();
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
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

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

app.Run();

static Delegate Query(Func<DashboardSnapshot, object> select) => async (IDashboardFacade facade, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(select(await facade.QueryAsync(cancellationToken))); }
    catch (DashboardUnavailableException) { return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Dashboard unavailable"); }
};

static void MapControl(RouteGroupBuilder group, string pattern, ContestControlAction action)
{
    group.MapPost(pattern, async (HttpContext context, IDashboardFacade facade, IAntiforgery antiforgery, IOptions<AccessOptions> options, CancellationToken cancellationToken) =>
    {
        var expectedOrigin = options.Value.PublicOrigin.TrimEnd('/');
        if (!context.Request.Headers.TryGetValue("Origin", out var origins) || origins.Count != 1 || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(origins.ToString()), System.Text.Encoding.UTF8.GetBytes(expectedOrigin)))
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

public partial class Program;
