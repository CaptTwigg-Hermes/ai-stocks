using AiStocks.Core;
using AiStocks.Research.Execution;

namespace AiStocks.Exhibition.Worker;

public sealed class HermesExhibitionModelInvoker(
    CredentialHomeFactory homeFactory,
    ExhibitionOptions options,
    IResearchProcessLauncher launcher) : IExhibitionModelInvoker
{
    public async Task<ResearchExecutionResult> InvokeAsync(AgentDefinition agent, string prompt, CancellationToken cancellationToken)
    {
        await using var home = await homeFactory.CreateAsync(agent, cancellationToken).ConfigureAwait(false);
        var attestationDirectory = Path.Combine(options.HermesHomeRoot, ".attestations", agent.Id.ToString("N"), Guid.NewGuid().ToString("N"));
        try
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = home.Path,
                ["HERMES_HOME"] = home.Path,
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin",
                ["LANG"] = "C.UTF-8",
                ["LC_ALL"] = "C.UTF-8",
                ["SEARXNG_URL"] = options.SearchBaseUrl.GetLeftPart(UriPartial.Authority)
            };
            var runner = new HermesResearchRunner(launcher, new ResearchExecutionOptions
            {
                HermesExecutable = options.HermesExecutable,
                RuntimeAttestationDirectory = attestationDirectory,
                Environment = environment,
                AllowControlledUserConfig = true,
                Timeout = TimeSpan.FromMinutes(10),
                DrainTimeout = TimeSpan.FromSeconds(10),
                MaximumPromptBytes = 256 * 1024,
                MaximumOutputBytes = 256 * 1024,
                MaximumErrorBytes = 128 * 1024
            });
            return await runner.RunAsync(agent.Id, agent.ModelId, prompt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(attestationDirectory)) Directory.Delete(attestationDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
