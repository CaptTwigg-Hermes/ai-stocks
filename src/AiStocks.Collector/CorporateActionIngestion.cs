using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace AiStocks.Collector;

public sealed class PostgresCorporateActionIngestion(string connectionString, TimeProvider? timeProvider = null)
{
    private const int MaximumInputBytes = 1_048_576;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<Guid> IngestAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length is < 2 or > MaximumInputBytes)
            throw new InvalidOperationException("Corporate action input size is invalid.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Corporate action input is invalid JSON.", exception);
        }
        using (document)
        {
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Corporate action input must be an object.");

            var raw = payload.ToArray();
            var json = Encoding.UTF8.GetString(raw);
            var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT ingest_verified_corporate_action($1::jsonb,$2,$3::sha256_hex,$4)", connection);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json });
            command.Parameters.AddWithValue(raw);
            command.Parameters.AddWithValue(hash);
            command.Parameters.AddWithValue(clock.GetUtcNow());
            return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Corporate action ingestion returned no identity."));
        }
    }

    public async Task<int> IngestDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new InvalidOperationException("Corporate action input directory is unavailable.");
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            var payload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            await IngestAsync(payload, cancellationToken).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidOperationException("Corporate action input contains duplicate properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }
}
