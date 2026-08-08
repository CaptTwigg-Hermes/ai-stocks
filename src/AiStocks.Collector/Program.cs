using AiStocks.MarketData;
using AiStocks.Collector;

var builder = WebApplication.CreateBuilder(args);
var archivePath = builder.Configuration["ARCHIVE_PATH"] ?? "/data/nasdaq";
var artifactRoot = builder.Configuration["ARTIFACT_ROOT"] ?? AppContext.BaseDirectory;
var databaseUrl = builder.Configuration["COLLECTOR_DATABASE_URL"]
    ?? throw new InvalidOperationException("COLLECTOR_DATABASE_URL is required");
var firdsPath = builder.Configuration["FIRDS_STATE_PATH"] ?? Path.Combine(archivePath, "firds-state.json");
var seedPayloadPath = builder.Configuration["STATUS_SEED_PAYLOAD_PATH"] ?? Path.Combine(archivePath, "status-seed.json");
var seedSignaturePath = builder.Configuration["STATUS_SEED_SIGNATURE_PATH"] ?? Path.Combine(archivePath, "status-seed.sig");
var pinnedPublicKeyPath = builder.Configuration["STATUS_PINNED_PUBLIC_KEY_PATH"] ?? Path.Combine(archivePath, "status-seed-public.der");
var pinnedKeyId = builder.Configuration["STATUS_PINNED_KEY_ID"] ?? throw new InvalidOperationException("STATUS_PINNED_KEY_ID is required");
StockholmCalendar.VerifyPinnedArtifacts(artifactRoot);

builder.Services.AddSingleton(new ImmutableArchive(archivePath));
builder.Services.AddSingleton(new SessionManifestStore(archivePath));
builder.Services.AddSingleton(new DurableFirdsStore(firdsPath));
builder.Services.AddSingleton(_ => new PinnedStatusSeedVerifier(pinnedKeyId, File.ReadAllBytes(pinnedPublicKeyPath))
    .Load(File.ReadAllText(seedPayloadPath), File.ReadAllBytes(seedSignaturePath), Path.Combine(archivePath, "status-state.json")));
builder.Services.AddSingleton(new CollectorHealth(TimeSpan.FromSeconds(120)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<NasdaqPostTradeClient>(client =>
{
    client.BaseAddress = new Uri("https://tradereports.nasdaq.com");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ai-stocks-collector/1.0 official-delayed-feed");
});
builder.Services.AddSingleton<NasdaqCollector>();
builder.Services.AddSingleton(serviceProvider => new PostgresCollectorPersistence(databaseUrl,
    serviceProvider.GetRequiredService<ImmutableArchive>(), serviceProvider.GetRequiredService<SessionManifestStore>(),
    serviceProvider.GetRequiredService<DurableFirdsStore>(), serviceProvider.GetRequiredService<NasdaqStatusMachine>(),
    seedPayloadPath, seedSignaturePath));
builder.Services.AddSingleton(new PostgresCollectorReadiness(databaseUrl));
builder.Services.AddHostedService<CollectorWorker>();

var app = builder.Build();
app.MapGet("/healthz", (CollectorHealth health) => health.IsHealthy(DateTimeOffset.UtcNow)
    ? Results.Ok(new { status = "healthy" })
    : Results.Json(new { status = "unhealthy", failure = health.Failure }, statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/readyz", async (PostgresCollectorReadiness readiness, CancellationToken cancellationToken) =>
{
    var result = await readiness.EvaluateAsync(DateTimeOffset.UtcNow, cancellationToken);
    return result.Ready ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "not-ready", failures = result.Failures }, statusCode: StatusCodes.Status503ServiceUnavailable);
});
if (args.Contains("--print-endpoints", StringComparer.Ordinal))
{
    var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
        .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
            .Select(method => new { method, path = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/') }))
        .OrderBy(endpoint => endpoint.path, StringComparer.Ordinal).ThenBy(endpoint => endpoint.method, StringComparer.Ordinal);
    Console.WriteLine("AISTOCKS_ENDPOINTS=" + System.Text.Json.JsonSerializer.Serialize(endpoints));
    return;
}
app.Run();

public partial class Program;
