namespace AiStocks.Exhibition.Worker;

public sealed record ExhibitionOptions
{
    public required Uri ApiBaseUrl { get; init; }
    public required string InternalKey { get; init; }
    public required string CopilotCredentialFile { get; init; }
    public Uri SearchBaseUrl { get; init; } = new("http://search:8080");
    public string HermesHomeRoot { get; init; } = "/dev/shm/aistocks-exhibition";
    public string StrategyMemoryRoot { get; init; } = "/var/lib/aistocks-exhibition/strategy-memory";
    public string HermesExecutable { get; init; } = "/opt/hermes/bin/hermes";
    public TimeSpan CycleInterval { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumApiResponseBytes { get; init; } = 2 * 1024 * 1024;

    public void Validate()
    {
        if (!ApiBaseUrl.IsAbsoluteUri || ApiBaseUrl.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("ApiBaseUrl must be an absolute HTTP(S) URI.");
        if (string.IsNullOrWhiteSpace(InternalKey) || InternalKey.Length < 32)
            throw new InvalidOperationException("InternalKey must contain at least 32 characters.");
        if (!Path.IsPathFullyQualified(CopilotCredentialFile) ||
            !Path.IsPathFullyQualified(HermesHomeRoot) ||
            !Path.IsPathFullyQualified(StrategyMemoryRoot) ||
            !Path.IsPathFullyQualified(HermesExecutable))
            throw new InvalidOperationException("Credential, HERMES_HOME root, StrategyMemoryRoot, and executable paths must be absolute.");
        var hermesRoot = Path.GetFullPath(HermesHomeRoot).TrimEnd(Path.DirectorySeparatorChar);
        var memoryRoot = Path.GetFullPath(StrategyMemoryRoot).TrimEnd(Path.DirectorySeparatorChar);
        var memoryRelativeToHermes = Path.GetRelativePath(hermesRoot, memoryRoot);
        if (memoryRelativeToHermes == "." ||
            !memoryRelativeToHermes.Equals("..", StringComparison.Ordinal) &&
            !memoryRelativeToHermes.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("StrategyMemoryRoot must be separate from the ephemeral HERMES_HOME root.");
        if (!SearchBaseUrl.IsAbsoluteUri || SearchBaseUrl.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(SearchBaseUrl.UserInfo) || !string.IsNullOrEmpty(SearchBaseUrl.Query) ||
            !string.IsNullOrEmpty(SearchBaseUrl.Fragment))
            throw new InvalidOperationException("SearchBaseUrl must be an absolute credential-free HTTP(S) origin.");
        if (CycleInterval < TimeSpan.FromMinutes(1) || CycleInterval > TimeSpan.FromHours(24))
            throw new InvalidOperationException("CycleInterval must be between one minute and 24 hours.");
        if (HttpTimeout < TimeSpan.FromSeconds(1) || HttpTimeout > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("HttpTimeout must be between one second and two minutes.");
        if (MaximumApiResponseBytes is < 1024 or > 16 * 1024 * 1024)
            throw new InvalidOperationException("MaximumApiResponseBytes is outside its safe bound.");
    }
}
