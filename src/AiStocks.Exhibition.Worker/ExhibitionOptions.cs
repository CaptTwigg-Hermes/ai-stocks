namespace AiStocks.Exhibition.Worker;

public sealed record ExhibitionOptions
{
    public required Uri ApiBaseUrl { get; init; }
    public required string InternalKey { get; init; }
    public required string CopilotCredentialFile { get; init; }
    public string HermesHomeRoot { get; init; } = "/dev/shm/aistocks-exhibition";
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
            !Path.IsPathFullyQualified(HermesExecutable))
            throw new InvalidOperationException("Credential, HERMES_HOME root, and executable paths must be absolute.");
        if (CycleInterval < TimeSpan.FromMinutes(1) || CycleInterval > TimeSpan.FromHours(24))
            throw new InvalidOperationException("CycleInterval must be between one minute and 24 hours.");
        if (HttpTimeout < TimeSpan.FromSeconds(1) || HttpTimeout > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("HttpTimeout must be between one second and two minutes.");
        if (MaximumApiResponseBytes is < 1024 or > 16 * 1024 * 1024)
            throw new InvalidOperationException("MaximumApiResponseBytes is outside its safe bound.");
    }
}
