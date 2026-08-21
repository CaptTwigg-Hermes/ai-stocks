using System.Text;
using System.Text.Json;
using AiStocks.Core;

namespace AiStocks.Exhibition.Worker;

public sealed record StrategyThesis(string Thesis, string Invalidation);

public sealed record StrategyUpdate(
    string Philosophy,
    IReadOnlyList<string> ResearchPlan,
    IReadOnlyList<string> EntryRules,
    IReadOnlyList<string> ExitRules,
    IReadOnlyList<string> RiskRules,
    IReadOnlyList<StrategyThesis> ActiveTheses,
    IReadOnlyList<string> Lessons,
    string JournalNote);

public sealed record StrategyJournalEntry(string RunId, string Note);

public sealed record AgentStrategyMemory(
    Guid AgentId,
    string Philosophy,
    IReadOnlyList<string> ResearchPlan,
    IReadOnlyList<string> EntryRules,
    IReadOnlyList<string> ExitRules,
    IReadOnlyList<string> RiskRules,
    IReadOnlyList<StrategyThesis> ActiveTheses,
    IReadOnlyList<string> Lessons,
    IReadOnlyList<StrategyJournalEntry> Journal);

public interface IStrategyMemoryStore
{
    AgentStrategyMemory? Load(AgentDefinition agent);
    void Save(AgentDefinition agent, string acceptedRunId, StrategyUpdate update);
}

public sealed class StrategyMemoryStore : IStrategyMemoryStore
{
    public const int MaximumFileBytes = 128 * 1024;
    public const int MaximumJournalEntries = 20;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string root;

    public StrategyMemoryStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
            throw new ArgumentException("Strategy memory root must be absolute.", nameof(rootPath));
        root = Path.GetFullPath(rootPath);
    }

    public AgentStrategyMemory? Load(AgentDefinition agent)
    {
        RequireFixedAgent(agent);
        lock (gate) return LoadFile(agent);
    }

    public void Save(AgentDefinition agent, string acceptedRunId, StrategyUpdate update)
    {
        RequireFixedAgent(agent);
        ArgumentNullException.ThrowIfNull(update);
        ValidateText(acceptedRunId, nameof(acceptedRunId), 128);
        ValidateUpdate(update);

        lock (gate)
        {
            Directory.CreateDirectory(root);
            var current = LoadFile(agent);
            if (current?.Journal.Any(entry => StringComparer.Ordinal.Equals(entry.RunId, acceptedRunId)) == true)
                return;
            var journal = (current?.Journal ?? [])
                .Append(new StrategyJournalEntry(acceptedRunId, update.JournalNote))
                .TakeLast(MaximumJournalEntries)
                .ToArray();
            var memory = new AgentStrategyMemory(agent.Id, update.Philosophy, update.ResearchPlan.ToArray(),
                update.EntryRules.ToArray(), update.ExitRules.ToArray(), update.RiskRules.ToArray(),
                update.ActiveTheses.ToArray(), update.Lessons.ToArray(), journal);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(memory, JsonOptions);
            if (bytes.Length > MaximumFileBytes)
                throw new InvalidDataException("Strategy memory exceeds its file-size bound.");

            var path = PathFor(agent.Id);
            var temporary = Path.Combine(root, $".{agent.Id:D}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           16 * 1024, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private AgentStrategyMemory? LoadFile(AgentDefinition agent)
    {
        var path = PathFor(agent.Id);
        if (!File.Exists(path)) return null;
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.SequentialScan);
            if (stream.Length is 0 or > MaximumFileBytes)
                throw new InvalidDataException("Strategy memory is empty or oversized.");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            var json = StrictUtf8.GetString(bytes);
            if (json.Contains('\0', StringComparison.Ordinal))
                throw new InvalidDataException("Strategy memory contains NUL data.");
            using var document = StrictJson.Parse(json, MaximumFileBytes);
            ValidateDocumentShape(document.RootElement);
            var memory = JsonSerializer.Deserialize<AgentStrategyMemory>(json, JsonOptions)
                ?? throw new InvalidDataException("Strategy memory is null.");
            if (memory.AgentId != agent.Id)
                throw new InvalidDataException("Strategy memory belongs to a different agent.");
            ValidateMemory(memory);
            return memory;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or EndOfStreamException or InvalidOperationException)
        {
            throw new InvalidDataException("Strategy memory is malformed or truncated.", exception);
        }
    }

    private static void ValidateDocumentShape(JsonElement root)
    {
        RequireProperties(root, "agentId", "philosophy", "researchPlan", "entryRules", "exitRules", "riskRules", "activeTheses", "lessons", "journal");
        foreach (var thesis in root.GetProperty("activeTheses").EnumerateArray())
            RequireProperties(thesis, "thesis", "invalidation");
        foreach (var entry in root.GetProperty("journal").EnumerateArray())
            RequireProperties(entry, "runId", "note");
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Strategy memory object shape is invalid.");
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length || actual.Any(name => !names.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("Strategy memory contains missing or unknown properties.");
    }

    private static void ValidateMemory(AgentStrategyMemory memory)
    {
        var update = new StrategyUpdate(memory.Philosophy, memory.ResearchPlan, memory.EntryRules,
            memory.ExitRules, memory.RiskRules, memory.ActiveTheses, memory.Lessons,
            memory.Journal.LastOrDefault()?.Note ?? "none");
        ValidateUpdate(update);
        if (memory.Journal.Count > MaximumJournalEntries)
            throw new InvalidDataException("Strategy journal exceeds its entry bound.");
        var runIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in memory.Journal)
        {
            ValidateText(entry.RunId, "journal runId", 128);
            ValidateText(entry.Note, "journal note", 1_000);
            if (!runIds.Add(entry.RunId)) throw new InvalidDataException("Strategy journal contains duplicate run IDs.");
        }
    }

    internal static void ValidateUpdate(StrategyUpdate update)
    {
        ValidateText(update.Philosophy, "philosophy", 2_000);
        ValidateList(update.ResearchPlan, "researchPlan");
        ValidateList(update.EntryRules, "entryRules");
        ValidateList(update.ExitRules, "exitRules");
        ValidateList(update.RiskRules, "riskRules");
        ValidateList(update.Lessons, "lessons");
        if (update.ActiveTheses is null || update.ActiveTheses.Count > 8)
            throw new InvalidDataException("activeTheses exceeds its item bound.");
        foreach (var thesis in update.ActiveTheses)
        {
            if (thesis is null) throw new InvalidDataException("activeTheses contains null.");
            ValidateText(thesis.Thesis, "thesis", 1_000);
            ValidateText(thesis.Invalidation, "invalidation", 1_000);
        }
        ValidateText(update.JournalNote, "journalNote", 1_000);
    }

    private static void ValidateList(IReadOnlyList<string> values, string name)
    {
        if (values is null || values.Count > 8) throw new InvalidDataException($"{name} exceeds its item bound.");
        foreach (var value in values) ValidateText(value, name, 500);
    }

    private static void ValidateText(string? value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0', StringComparison.Ordinal))
            throw new InvalidDataException($"{name} is empty, oversized, or contains NUL data.");
    }

    private static void RequireFixedAgent(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (!ContestContract.IsExactAgent(agent.Id, agent.ModelId))
            throw new InvalidOperationException("Strategy memory requires a fixed exhibition agent.");
    }

    private string PathFor(Guid agentId) => Path.Combine(root, agentId.ToString("D") + ".json");
}
