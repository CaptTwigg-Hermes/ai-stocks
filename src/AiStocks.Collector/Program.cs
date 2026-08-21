using AiStocks.MarketData;
using AiStocks.Collector;
using AiStocks.Observability;

if (args.Contains("--replay-archive", StringComparer.Ordinal))
{
    try
    {
        if (args.Length != 2 || args[0] != "--replay-archive")
            throw new ArgumentException("Usage: AiStocks.Collector --replay-archive <archive-root>");
        var replay = NasdaqArchiveReplay.Replay(args[1]);
        Console.WriteLine($"NASDAQ_ARCHIVE_REPLAY_OK reports={replay.Reports} rows={replay.Rows}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine("NASDAQ_ARCHIVE_REPLAY_FAILED " + exception.Message);
        Environment.ExitCode = 1;
    }
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.UseAiStocksSerilog("AiStocks.Collector");
var archivePath = builder.Configuration["ARCHIVE_PATH"] ?? "/data/nasdaq";
var artifactRoot = builder.Configuration["ARTIFACT_ROOT"] ?? AppContext.BaseDirectory;
var databaseUrl = builder.Configuration["COLLECTOR_DATABASE_URL"]
    ?? throw new InvalidOperationException("COLLECTOR_DATABASE_URL is required");
var firdsPath = builder.Configuration["FIRDS_STATE_PATH"] ?? Path.Combine(archivePath, "firds-state.json");
var nordicFirdsPath = builder.Configuration["NORDIC_FIRDS_STATE_PATH"]
    ?? Path.Combine(archivePath, "firds-nordic-state.json");
var collectNordicExhibition = builder.Configuration.GetValue("COLLECT_NORDIC_EXHIBITION", false);
var firdsPlanPath = builder.Configuration["FIRDS_ACQUISITION_PLAN_PATH"]
    ?? throw new InvalidOperationException("FIRDS_ACQUISITION_PLAN_PATH is required");
StockholmCalendar.VerifyPinnedArtifacts(artifactRoot);

var strictFirds = new DurableFirdsStore(firdsPath);
var nordicFirds = collectNordicExhibition
    ? new DurableFirdsStore(nordicFirdsPath, FirdsUniverse.NordicExhibition)
    : null;
var ecbFx = new EcbFxStore(archivePath);
var unsupportedCorporateActions = new UnsupportedCorporateActionStore(archivePath);
builder.Services.AddSingleton(new ImmutableArchive(archivePath));
builder.Services.AddSingleton(new SessionManifestStore(archivePath));
builder.Services.AddSingleton(strictFirds);
builder.Services.AddSingleton(ecbFx);
builder.Services.AddSingleton(_ => NasdaqStatusMachine.LoadPublicRssBestEffort(Path.Combine(archivePath, "status-state.json")));
var collectorPollSeconds = builder.Configuration.GetValue("COLLECTOR_POLL_SECONDS", 15);
builder.Services.AddSingleton(new CollectorHealth(
    PostgresCollectorReadiness.StalenessWindow(collectorPollSeconds)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<NasdaqPostTradeClient>(client =>
{
    client.BaseAddress = new Uri("https://tradereports.nasdaq.com");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ai-stocks-collector/1.0 official-delayed-feed");
});
builder.Services.AddHttpClient("market-reference", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ai-stocks-collector/1.0 authoritative-reference-acquisition");
}).ConfigurePrimaryHttpMessageHandler(MarketReferenceAcquirer.CreatePrimaryHandler);
builder.Services.AddHttpClient("ecb-fx", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ai-stocks-collector/1.0 ecb-reference-fx");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton(serviceProvider => new MarketReferenceAcquirer(
    serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("market-reference"),
    strictFirds, serviceProvider.GetRequiredService<NasdaqStatusMachine>(),
    firdsPlanPath, Path.Combine(archivePath, "status-rss"), nordicFirds));
builder.Services.AddSingleton(serviceProvider => new EcbFxAcquirer(
    serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("ecb-fx"), ecbFx));
builder.Services.AddSingleton(unsupportedCorporateActions);
builder.Services.AddSingleton(serviceProvider => new NasdaqCollector(
    serviceProvider.GetRequiredService<NasdaqPostTradeClient>(), serviceProvider.GetRequiredService<ImmutableArchive>(),
    serviceProvider.GetRequiredService<SessionManifestStore>(), new CollectorDownloadPolicy(
        builder.Configuration.GetValue("COLLECTOR_MAX_DOWNLOADS_PER_POLL", 32),
        TimeSpan.FromMinutes(builder.Configuration.GetValue("COLLECTOR_CONFLICT_REFETCH_MINUTES", 360)))));
builder.Services.AddSingleton(serviceProvider => new PostgresCollectorPersistence(databaseUrl,
    serviceProvider.GetRequiredService<ImmutableArchive>(), serviceProvider.GetRequiredService<SessionManifestStore>(),
    serviceProvider.GetRequiredService<DurableFirdsStore>(), serviceProvider.GetRequiredService<NasdaqStatusMachine>(),
    null, null));
builder.Services.AddSingleton(new PostgresCorporateActionIngestion(databaseUrl));
builder.Services.AddSingleton(new PostgresCollectorReadiness(databaseUrl, collectorPollSeconds));
builder.Services.AddHostedService<CollectorWorker>();

var app = builder.Build();
app.UseAiStocksRequestLogging();
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
