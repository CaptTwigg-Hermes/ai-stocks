using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace AiStocks.Api;

public sealed class PostgresPreviewRaceStatePersistence(NpgsqlDataSource dataSource) : IPreviewRaceStatePersistence
{
    private const int MaximumStateCharacters = 4 * 1024 * 1024;

    public PreviewRacePersistedState? Load()
    {
        try
        {
            using var command = dataSource.CreateCommand(
                "SELECT revision,state_json::text FROM exhibition_preview_state WHERE singleton=true");
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var json = reader.GetString(1);
            ValidateJson(json);
            return new(reader.GetInt64(0), json);
        }
        catch (Exception exception) when (exception is NpgsqlException or JsonException)
        {
            _ = exception;
            throw new PreviewRacePersistenceException("Could not load durable exhibition state.");
        }
    }

    public PreviewRacePersistedState Save(long? expectedRevision, string json, Guid mutationId)
    {
        ValidateJson(json);
        try
        {
            using var connection = dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = expectedRevision is null
                ? """
                    INSERT INTO exhibition_preview_state(singleton,revision,state_json,updated_at)
                    VALUES(true,1,$1::jsonb,clock_timestamp())
                    ON CONFLICT (singleton) DO NOTHING
                    RETURNING revision
                    """
                : """
                    UPDATE exhibition_preview_state
                    SET revision=revision+1,state_json=$1::jsonb,updated_at=clock_timestamp()
                    WHERE singleton=true AND revision=$2
                    RETURNING revision
                    """;
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = json, NpgsqlDbType = NpgsqlDbType.Jsonb });
            if (expectedRevision is not null) command.Parameters.AddWithValue(expectedRevision.Value);
            var revision = command.ExecuteScalar();
            if (revision is not long persistedRevision)
                throw new PreviewRacePersistenceException("Concurrent durable exhibition state update was rejected.");
            using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = """
                INSERT INTO exhibition_preview_mutation_receipts(mutation_id,state_revision)
                VALUES($1,$2)
                """;
            receipt.Parameters.AddWithValue(mutationId);
            receipt.Parameters.AddWithValue(persistedRevision);
            receipt.ExecuteNonQuery();
            transaction.Commit();
            return new(persistedRevision, json);
        }
        catch (PreviewRacePersistenceException)
        {
            throw;
        }
        catch (NpgsqlException exception)
        {
            _ = exception;
            throw new PreviewRacePersistenceException("Could not save durable exhibition state.");
        }
    }

    public bool WasCommitted(Guid mutationId)
    {
        try
        {
            using var command = dataSource.CreateCommand("""
                SELECT EXISTS(
                  SELECT 1 FROM exhibition_preview_mutation_receipts WHERE mutation_id=$1
                )
                """);
            command.Parameters.AddWithValue(mutationId);
            return command.ExecuteScalar() is true;
        }
        catch (NpgsqlException exception)
        {
            _ = exception;
            throw new PreviewRacePersistenceException("Could not verify durable exhibition mutation.");
        }
    }

    private static void ValidateJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumStateCharacters)
            throw new PreviewRacePersistenceException("Durable exhibition state is empty or oversized.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new PreviewRacePersistenceException("Durable exhibition state must be one JSON object.");
    }
}
