using System.Text.Json;
using AiStocks.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.UseAiStocksSerilog("AiStocks.Ui");
var apiOrigin = builder.Configuration["API_PUBLIC_ORIGIN"];
if (string.IsNullOrWhiteSpace(apiOrigin) && builder.Environment.IsDevelopment())
    apiOrigin = "http://192.168.50.2:3233";
if (!Uri.TryCreate(apiOrigin, UriKind.Absolute, out var apiUri)
    || (apiUri.Scheme != Uri.UriSchemeHttps && !(builder.Environment.IsDevelopment() && apiUri.Scheme == Uri.UriSchemeHttp))
    || !string.IsNullOrEmpty(apiUri.UserInfo) || apiUri.AbsolutePath != "/"
    || !string.IsNullOrEmpty(apiUri.Query) || !string.IsNullOrEmpty(apiUri.Fragment))
    throw new InvalidOperationException("API_PUBLIC_ORIGIN must be an exact HTTPS origin (HTTP is development-only).");
apiOrigin = apiUri.GetLeftPart(UriPartial.Authority);

var app = builder.Build();
app.UseAiStocksRequestLogging();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self' " + apiOrigin + "; img-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next(context);
});
app.MapGet("/runtime-config.js", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Text($"window.AISTOCKS_API_URL={JsonSerializer.Serialize(apiOrigin)};",
        "text/javascript; charset=utf-8", statusCode: StatusCodes.Status200OK);
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-store";
    }
});
app.MapGet("/healthz", () => Results.Ok(new { status = "ready" }));
app.MapGet("/trade", () => Results.File("trade.html", "text/html; charset=utf-8"));
app.MapFallbackToFile("index.html");

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
