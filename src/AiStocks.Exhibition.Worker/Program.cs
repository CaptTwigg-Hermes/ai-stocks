using System.Text.Json;
using AiStocks.Exhibition.Worker;
using AiStocks.Research.Evidence;
using AiStocks.Research.Execution;

var runOnce = args.Contains("--run-once", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args);
var options = LoadOptions(builder.Configuration);
options.Validate();
RuntimePrerequisites.Verify(options);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ExhibitionHealthState>();
builder.Services.AddSingleton(new CredentialHomeFactory(options.HermesHomeRoot, options.CopilotCredentialFile));
builder.Services.AddSingleton<IResearchProcessLauncher, SystemResearchProcessLauncher>();
builder.Services.AddSingleton<IExhibitionModelInvoker, HermesExhibitionModelInvoker>();
builder.Services.AddSingleton<IHostResolver, SystemHostResolver>();
builder.Services.AddSingleton<IEvidenceHttpTransport>(_ => new PinnedAddressHttpTransport(TimeSpan.FromSeconds(5)));
builder.Services.AddSingleton<IEvidenceVerifier, EvidenceVerifier>();
builder.Services.AddHttpClient<IExhibitionApi, ExhibitionApiClient>(client =>
{
    client.BaseAddress = options.ApiBaseUrl;
    client.Timeout = Timeout.InfiniteTimeSpan;
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    ConnectTimeout = TimeSpan.FromSeconds(5),
    UseCookies = false
});
builder.Services.AddSingleton<ExhibitionCycle>();
if (!runOnce) builder.Services.AddHostedService<ExhibitionSchedulerService>();

var app = builder.Build();
var health = app.Services.GetRequiredService<ExhibitionHealthState>();
health.PrerequisitesReady();
app.MapGet("/healthz", (ExhibitionHealthState state) =>
{
    var snapshot = state.Snapshot();
    return snapshot.Status == "ready"
        ? Results.Ok(snapshot)
        : Results.Json(snapshot, statusCode: StatusCodes.Status503ServiceUnavailable);
});

if (args.Contains("--print-endpoints", StringComparer.Ordinal))
{
    var endpoints = ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
        .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
            .Select(method => new { method, path = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/') }))
        .OrderBy(endpoint => endpoint.path, StringComparer.Ordinal).ThenBy(endpoint => endpoint.method, StringComparer.Ordinal);
    Console.WriteLine("AISTOCKS_EXHIBITION_ENDPOINTS=" + JsonSerializer.Serialize(endpoints));
    return;
}

if (runOnce)
{
    var result = await app.Services.GetRequiredService<ExhibitionCycle>()
        .RunAsync(app.Services.GetRequiredService<TimeProvider>().GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine("AISTOCKS_EXHIBITION_RUN=" + JsonSerializer.Serialize(result));
    Environment.ExitCode = result.Failures.Count == 0 ? 0 : 1;
    return;
}

await app.RunAsync().ConfigureAwait(false);

static ExhibitionOptions LoadOptions(IConfiguration configuration)
{
    static string Required(IConfiguration values, string key, string alias) =>
        values[key] is { Length: > 0 } value ? value :
        values[alias] is { Length: > 0 } aliased ? aliased :
        throw new InvalidOperationException($"{key} ({alias}) is required.");
    return new ExhibitionOptions
    {
        ApiBaseUrl = new Uri(Required(configuration, "Exhibition:ApiBaseUrl", "AI_EXHIBITION_API_ORIGIN"), UriKind.Absolute),
        InternalKey = Required(configuration, "Exhibition:InternalKey", "AI_EXHIBITION_KEY"),
        CopilotCredentialFile = Required(configuration, "Exhibition:CopilotCredentialFile", "HERMES_CREDENTIAL_FILE"),
        HermesHomeRoot = configuration["Exhibition:HermesHomeRoot"] ?? "/dev/shm/aistocks-exhibition",
        HermesExecutable = configuration["Exhibition:HermesExecutable"] ?? "/opt/hermes/bin/hermes",
        CycleInterval = TimeSpan.FromSeconds(configuration.GetValue<int?>("Exhibition:CycleIntervalSeconds") ??
            configuration.GetValue("AI_EXHIBITION_INTERVAL_SECONDS", 3600)),
        HttpTimeout = TimeSpan.FromSeconds(configuration.GetValue("Exhibition:HttpTimeoutSeconds", 30)),
        MaximumApiResponseBytes = configuration.GetValue("Exhibition:MaximumApiResponseBytes", 2 * 1024 * 1024)
    };
}

public static class RuntimePrerequisites
{
    public static void Verify(ExhibitionOptions options)
    {
        using (File.Open(options.CopilotCredentialFile, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
        if (!File.Exists(options.HermesExecutable)) throw new InvalidOperationException("Hermes executable is missing.");
        Directory.CreateDirectory(options.HermesHomeRoot);
        if (!OperatingSystem.IsWindows() && !IsTmpfs(options.HermesHomeRoot))
            throw new InvalidOperationException("HermesHomeRoot must reside on tmpfs.");
    }

    private static bool IsTmpfs(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('/');
        var bestLength = -1;
        string? bestType = null;
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;
            var left = line[..separator].Split(' ');
            var right = line[(separator + 3)..].Split(' ');
            if (left.Length < 5 || right.Length < 1) continue;
            var mount = left[4].Replace("\\040", " ", StringComparison.Ordinal).TrimEnd('/');
            if ((full == mount || full.StartsWith(mount + "/", StringComparison.Ordinal)) && mount.Length > bestLength)
            {
                bestLength = mount.Length;
                bestType = right[0];
            }
        }
        return bestType == "tmpfs";
    }
}

public partial class Program;
