using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace AiStocks.Persistence;

public sealed record PersistableResearchAttestation
{
    public required Guid Id { get; init; }
    public Guid? AgentRunId { get; init; }
    public Guid? OrderId { get; init; }
    public required Guid AgentId { get; init; }
    public required string RequestedModelId { get; init; }
    public required string RequestedProvider { get; init; }
    public required string ActualModelId { get; init; }
    public required string ActualProvider { get; init; }
    public required string InvocationJson { get; init; }
    public required string InvocationSha256 { get; init; }
    public required ImmutableArray<byte> RuntimeReport { get; init; }
    public required string RuntimeReportSha256 { get; init; }
    public required string EvidenceJson { get; init; }
    public required string EvidenceSha256 { get; init; }
    public required DateTimeOffset AttestedAt { get; init; }
}

public sealed class ResearchAttestationStore
{
    public async Task<Guid> PersistAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PersistableResearchAttestation attestation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(attestation);
        Validate(attestation);

        await using var command = new NpgsqlCommand("""
            SELECT persist_research_attestation(
              $1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb,$10::sha256_hex,$11,$12::sha256_hex,
              $13::jsonb,$14::sha256_hex,$15)
            """, connection, transaction);
        AddUuid(command, attestation.Id);
        AddNullableUuid(command, attestation.AgentRunId);
        AddNullableUuid(command, attestation.OrderId);
        AddUuid(command, attestation.AgentId);
        command.Parameters.AddWithValue(attestation.RequestedModelId);
        command.Parameters.AddWithValue(attestation.RequestedProvider);
        command.Parameters.AddWithValue(attestation.ActualModelId);
        command.Parameters.AddWithValue(attestation.ActualProvider);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, attestation.InvocationJson);
        command.Parameters.AddWithValue(attestation.InvocationSha256);
        command.Parameters.AddWithValue(NpgsqlDbType.Bytea, attestation.RuntimeReport.ToArray());
        command.Parameters.AddWithValue(attestation.RuntimeReportSha256);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, attestation.EvidenceJson);
        command.Parameters.AddWithValue(attestation.EvidenceSha256);
        command.Parameters.AddWithValue(attestation.AttestedAt);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL did not return the persisted attestation ID."));
    }

    private static void Validate(PersistableResearchAttestation value)
    {
        if (value.AgentRunId is null && value.OrderId is null ||
            !StringComparer.Ordinal.Equals(value.RequestedModelId, value.ActualModelId) ||
            !StringComparer.Ordinal.Equals(value.RequestedProvider, "copilot") ||
            !StringComparer.Ordinal.Equals(value.ActualProvider, "copilot") ||
            value.RuntimeReport.IsDefaultOrEmpty ||
            !HashMatches(value.InvocationJson, value.InvocationSha256) ||
            !HashMatches(value.RuntimeReport.AsSpan(), value.RuntimeReportSha256) ||
            !HashMatches(value.EvidenceJson, value.EvidenceSha256))
            throw new InvalidOperationException("Research attestation is incomplete, mismatched, or corrupt.");
    }

    private static bool HashMatches(string value, string expected) =>
        HashMatches(Encoding.UTF8.GetBytes(value), expected);

    private static bool HashMatches(ReadOnlySpan<byte> value, string expected) =>
        expected.Length == 64 && StringComparer.Ordinal.Equals(
            Convert.ToHexStringLower(SHA256.HashData(value)), expected);

    private static void AddUuid(NpgsqlCommand command, Guid value) =>
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, value);

    private static void AddNullableUuid(NpgsqlCommand command, Guid? value)
    {
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = value is null ? DBNull.Value : value.Value
        });
    }
}
