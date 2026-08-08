using AiStocks.Core;
using AiStocks.Persistence;
using AiStocks.Research.Decisions;
using AiStocks.Research.Evidence;
using AiStocks.Research.Execution;
using AiStocks.Worker;
using AiStocks.Worker.Orchestration;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = PostgresConfiguration.Require(PostgresConfiguration.Environment(), "DATABASE_URL");
var dataSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PostgresWorkerState>();
builder.Services.AddSingleton<IDurableRunSchedulePort>(services => services.GetRequiredService<PostgresWorkerState>());
builder.Services.AddSingleton<IRunStore>(services => services.GetRequiredService<PostgresWorkerState>());
builder.Services.AddSingleton<IContestPausePort>(services => services.GetRequiredService<PostgresWorkerState>());
builder.Services.AddSingleton<IAgentContextPort>(services => services.GetRequiredService<PostgresWorkerState>());
builder.Services.AddSingleton<IAgentDecisionPort>(services => services.GetRequiredService<PostgresWorkerState>());
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<SystemResearchProcessLauncher>();
builder.Services.AddSingleton<IHostResolver, SystemHostResolver>();
builder.Services.AddSingleton<IEvidenceHttpTransport, PinnedAddressHttpTransport>();
builder.Services.AddSingleton<IEvidenceVerifier, EvidenceVerifier>();
builder.Services.AddSingleton<ResearchDecisionAttestor>();
builder.Services.AddSingleton(services => new HermesResearchRunner(
    services.GetRequiredService<SystemResearchProcessLauncher>(),
    new ResearchExecutionOptions
    {
        HermesExecutable = builder.Configuration["HermesExecutable"] ?? "/opt/hermes/bin/hermes"
    }));
builder.Services.AddSingleton<IAgentRunner, HermesAgentRunner>();
builder.Services.AddSingleton<DurableOrchestrator>();
builder.Services.AddHostedService<WorkerRuntimeService>();

var app = builder.Build();
app.MapGet("/healthz", async (PostgresWorkerState state, CancellationToken cancellationToken) =>
    await state.ReadyAsync(cancellationToken).ConfigureAwait(false)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable));
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

public sealed class SystemClock(TimeProvider timeProvider) : IClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}

public partial class Program;
