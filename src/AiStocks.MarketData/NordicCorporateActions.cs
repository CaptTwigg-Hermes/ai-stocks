using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiStocks.MarketData;

public sealed record UnsupportedCorporateAction(
    string Venue,
    string Isin,
    string OrderBookId,
    string ActionType,
    DateTimeOffset EffectiveAt,
    string Sha256,
    string RawPath);

public sealed record UnsupportedCorporateActionSnapshot(
    DateTimeOffset RefreshedAt,
    IReadOnlyList<UnsupportedCorporateAction> Actions);

public sealed partial class UnsupportedCorporateActionStore
{
    private const int MaximumFiles = 256;
    private const int MaximumFileBytes = 1_048_576;
    private const int MaximumTotalBytes = 4 * 1_048_576;
    private const int MaximumStateBytes = MaximumTotalBytes + 1_048_576;
    private const int LinuxOpenReadOnly = 0;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int LinuxOpenNoFollow = 0x20000;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> ActionTypes =
        ["DIVIDEND", "SPLIT", "CASH_MERGER", "STOCK_MERGER", "DELISTING", "CORRECTION"];
    private static readonly IReadOnlyDictionary<string, string> VenueCurrencies =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XSTO"] = "SEK",
            ["XCSE"] = "DKK",
            ["XHEL"] = "EUR",
            ["ONSE"] = "NOK",
            ["XICE"] = "ISK"
        };
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string statePath;

    public UnsupportedCorporateActionStore(string archivePath) =>
        statePath = Path.Combine(Path.GetFullPath(archivePath), "nordic-unsupported-corporate-actions.json");

    public UnsupportedCorporateActionSnapshot RefreshFromDirectory(string inputDirectory, DateTimeOffset refreshedAt)
    {
        if (!Path.IsPathFullyQualified(inputDirectory) || !Directory.Exists(inputDirectory) || refreshedAt == default)
            throw new MarketDataException("Nordic corporate-action input is unavailable");
        var paths = Directory.EnumerateFiles(inputDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal).Take(MaximumFiles + 1).ToArray();
        if (paths.Length > MaximumFiles)
            throw new MarketDataException("Nordic corporate-action input exceeds its file bound");

        var actions = new List<UnsupportedCorporateAction>(paths.Length);
        var totalBytes = 0;
        foreach (var path in paths)
        {
            var bytes = ReadBoundedRegularFile(path, MaximumFileBytes,
                "Nordic corporate-action input file is invalid");
            totalBytes = checked(totalBytes + bytes.Length);
            if (totalBytes > MaximumTotalBytes)
                throw new MarketDataException("Nordic corporate-action input exceeds its byte bound");
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var rawPath = ArchiveRaw(bytes, sha256);
            actions.Add(Parse(bytes, sha256, rawPath));
        }

        var duplicates = actions.GroupBy(action =>
            (action.Venue, action.Isin, action.OrderBookId, action.ActionType, action.EffectiveAt)).Any(group => group.Count() > 1);
        if (duplicates) throw new MarketDataException("Nordic corporate-action input contains duplicate actions");

        var current = File.Exists(statePath) ? LoadVerified(refreshedAt, requireFreshness: false) : null;
        if (current is not null)
        {
            if (refreshedAt <= current.RefreshedAt)
                throw new MarketDataException("Nordic corporate-action state cannot regress in time");
            foreach (var existing in current.Actions)
                if (!actions.Any(candidate => candidate == existing))
                    throw new MarketDataException("Nordic corporate-action history cannot be removed or changed");
        }

        var snapshot = new UnsupportedCorporateActionSnapshot(refreshedAt.ToUniversalTime(),
            actions.OrderBy(action => action.Venue, StringComparer.Ordinal)
                .ThenBy(action => action.Isin, StringComparer.Ordinal)
                .ThenBy(action => action.OrderBookId, StringComparer.Ordinal)
                .ThenBy(action => action.EffectiveAt).ToArray());
        Persist(snapshot);
        return snapshot;
    }

    public UnsupportedCorporateActionSnapshot LoadVerified(DateTimeOffset asOf) =>
        LoadVerified(asOf, requireFreshness: true);

    public bool IsBlocked(string venue, string isin, string orderBookId, DateTimeOffset asOf) =>
        LoadVerified(asOf).Actions.Any(action => action.Venue == venue && action.Isin == isin &&
            action.OrderBookId == orderBookId);

    private UnsupportedCorporateActionSnapshot LoadVerified(DateTimeOffset asOf, bool requireFreshness)
    {
        if (!File.Exists(statePath))
            throw new MarketDataException("Nordic corporate-action state is missing");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(ReadBoundedRegularFile(statePath,
                    MaximumStateBytes, "Nordic corporate-action state is malformed"), JsonOptions)
                ?? throw new JsonException();
            var stateBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.State, JsonOptions);
            if (!Sha256Pattern().IsMatch(envelope.Sha256) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stateBytes), Convert.FromHexString(envelope.Sha256)) ||
                envelope.State.RefreshedAt == default || envelope.State.RefreshedAt > asOf ||
                requireFreshness && asOf - envelope.State.RefreshedAt > MaximumAge ||
                envelope.State.Actions.Count > MaximumFiles)
                throw new MarketDataException("Nordic corporate-action state provenance or freshness is invalid");
            foreach (var action in envelope.State.Actions)
            {
                Validate(action);
                var rawRoot = Path.GetFullPath(statePath + ".raw") + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(action.RawPath).StartsWith(rawRoot, StringComparison.Ordinal))
                    throw new MarketDataException("Nordic corporate-action raw path escapes its archive");
                var raw = ReadBoundedRegularFile(action.RawPath, MaximumFileBytes,
                    "Nordic corporate-action raw archive is invalid");
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw), Convert.FromHexString(action.Sha256)))
                    throw new MarketDataException("Nordic corporate-action raw checksum is invalid");
                if (Parse(raw, action.Sha256, action.RawPath) != action)
                    throw new MarketDataException("Nordic corporate-action state disagrees with its raw input");
            }
            return envelope.State;
        }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException or
                                          OverflowException or InvalidOperationException or KeyNotFoundException)
        {
            throw new MarketDataException("Nordic corporate-action state is malformed", exception);
        }
    }

    private UnsupportedCorporateAction Parse(byte[] bytes, string sha256, string rawPath)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("schemaVersion").ToString() != "1")
            throw new MarketDataException("Nordic corporate-action input schema is invalid");
        var venue = root.TryGetProperty("venue", out var venueElement) ? venueElement.GetString() : "XSTO";
        var action = new UnsupportedCorporateAction(
            venue ?? string.Empty,
            root.GetProperty("isin").GetString() ?? string.Empty,
            root.GetProperty("orderBookId").GetString() ?? string.Empty,
            root.GetProperty("actionType").GetString() ?? string.Empty,
            root.GetProperty("effectiveAt").GetDateTimeOffset(),
            sha256,
            Path.GetFullPath(rawPath));
        Validate(action);
        return action;
    }

    private static void Validate(UnsupportedCorporateAction action)
    {
        if (!VenueCurrencies.ContainsKey(action.Venue) || !IsinPattern().IsMatch(action.Isin) ||
            string.IsNullOrWhiteSpace(action.OrderBookId) || action.OrderBookId.Length > 128 ||
            !ActionTypes.Contains(action.ActionType) || action.EffectiveAt == default ||
            !Sha256Pattern().IsMatch(action.Sha256) || !Path.IsPathFullyQualified(action.RawPath))
            throw new MarketDataException("Nordic corporate-action identity is invalid");
    }

    private string ArchiveRaw(byte[] bytes, string sha256)
    {
        var root = statePath + ".raw";
        var rawPath = Path.Combine(root, sha256 + ".json");
        if (File.Exists(rawPath))
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(ReadBoundedRegularFile(rawPath,
                        MaximumFileBytes, "Nordic corporate-action raw archive is invalid")),
                    Convert.FromHexString(sha256)))
                throw new MarketDataException("Nordic corporate-action raw archive conflicts");
        }
        else
        {
            Directory.CreateDirectory(root);
            AtomicFile.Write(rawPath, bytes);
        }
        return rawPath;
    }

    private void Persist(UnsupportedCorporateActionSnapshot snapshot)
    {
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var envelope = new Envelope(Convert.ToHexStringLower(SHA256.HashData(stateBytes)), snapshot);
        AtomicFile.Write(statePath, JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
    }

    private static byte[] ReadBoundedRegularFile(string path, int maximumBytes, string invalidMessage)
    {
        try
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new MarketDataException(invalidMessage);

            using var handle = OpenReadOnlyNoFollow(path, invalidMessage);
            var length = RandomAccess.GetLength(handle);
            if (length is < 2 || length > maximumBytes)
                throw new MarketDataException(invalidMessage);

            var bytes = new byte[(int)length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
                if (read == 0) throw new MarketDataException(invalidMessage);
                offset += read;
            }
            Span<byte> extra = stackalloc byte[1];
            if (RandomAccess.Read(handle, extra, offset) != 0)
                throw new MarketDataException(invalidMessage);
            return bytes;
        }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          OverflowException or NotSupportedException)
        {
            throw new MarketDataException(invalidMessage, exception);
        }
    }

    private static SafeFileHandle OpenReadOnlyNoFollow(string path, string invalidMessage)
    {
        if (!OperatingSystem.IsLinux())
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new MarketDataException(invalidMessage);
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOptions.SequentialScan);
        }

        var descriptor = OpenNoFollow(path, LinuxOpenReadOnly | LinuxOpenCloseOnExec | LinuxOpenNoFollow);
        if (descriptor < 0)
            throw new MarketDataException(invalidMessage,
                new IOException($"open failed with errno {Marshal.GetLastPInvokeError()}"));
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

#pragma warning disable SYSLIB1054 // DllImport avoids enabling unsafe code solely for this libc call.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNoFollow([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
#pragma warning restore SYSLIB1054

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new MarketDataException("Nordic corporate-action input contains duplicate properties");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private sealed record Envelope(string Sha256, UnsupportedCorporateActionSnapshot State);
    [GeneratedRegex("^[A-Z]{2}[A-Z0-9]{9}[0-9]$", RegexOptions.CultureInvariant)] private static partial Regex IsinPattern();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)] private static partial Regex Sha256Pattern();
}
