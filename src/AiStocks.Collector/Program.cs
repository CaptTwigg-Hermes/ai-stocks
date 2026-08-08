using AiStocks.MarketData;
using AiStocks.Collector;

var builder = WebApplication.CreateBuilder(args);
var archivePath = builder.Configuration["ArchivePath"] ?? "/data/nasdaq";
var artifactRoot = builder.Configuration["ArtifactRoot"] ?? AppContext.BaseDirectory;
StockholmCalendar.VerifyPinnedArtifacts(artifactRoot);

builder.Services.AddSingleton(new ImmutableArchive(archivePath));
builder.Services.AddSingleton(new SessionManifestStore(archivePath));
builder.Services.AddSingleton(new CollectorHealth(TimeSpan.FromSeconds(120)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<NasdaqPostTradeClient>(client =>
{
    client.BaseAddress = new Uri("https://tradereports.nasdaq.com");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ai-stocks-collector/1.0 official-delayed-feed");
});
builder.Services.AddSingleton<NasdaqCollector>();
builder.Services.AddHostedService<CollectorWorker>();
builder.Services.AddSingleton(new ConfiguredMarketDataReadiness(
    archivePath,
    builder.Configuration["FirdsStatePath"] ?? Path.Combine(archivePath, "firds-state.json"),
    builder.Configuration["ObservationStatePath"] ?? Path.Combine(archivePath, "observation-state.json"),
    builder.Configuration["StatusSeedPayloadPath"] ?? Path.Combine(archivePath, "status-seed.json"),
    builder.Configuration["StatusSeedSignaturePath"] ?? Path.Combine(archivePath, "status-seed.sig"),
    builder.Configuration["StatusPinnedPublicKeyPath"] ?? Path.Combine(archivePath, "status-seed-public.der"),
    builder.Configuration["StatusPinnedKeyId"] ?? string.Empty));

var app = builder.Build();
app.MapGet("/healthz", (CollectorHealth health) => health.IsHealthy(DateTimeOffset.UtcNow)
    ? Results.Ok(new { status = "healthy" })
    : Results.Json(new { status = "unhealthy", failure = health.Failure }, statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/readyz", (ConfiguredMarketDataReadiness readiness) =>
{
    var result = readiness.Evaluate(DateOnly.FromDateTime(DateTime.UtcNow));
    return result.Ready ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "not-ready", failures = result.Failures }, statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.Run();

public partial class Program;
