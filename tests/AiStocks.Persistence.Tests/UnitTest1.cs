using System.Text.Json;
using AiStocks.Persistence;

namespace AiStocks.Persistence.Tests;

public sealed class PersistenceContractTests
{
    private static readonly string Sql = string.Join('\n', MigrationCatalog.All.Select(migration => migration.Sql));

    [Fact]
    public void InitialMigrationDefinesRolesAndCompletePersistenceSurface()
    {
        Assert.Contains("ai_stocks_migrator", Sql);
        Assert.Contains("ai_stocks_runtime", Sql);
        string[] tables =
        [
            "schema_migrations", "agents", "contest_state", "contest_state_events",
            "instruments", "market_observations", "prompts", "agent_runs", "scheduled_agent_runs",
            "strategies", "orders", "order_outcomes", "ledger_events", "fills", "positions",
            "account_balances", "portfolio_snapshots", "corporate_actions", "corporate_action_applications",
            "final_rankings"
        ];
        foreach (var table in tables)
            Assert.Contains($"CREATE TABLE {table}", Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InitialMigrationSeedsExactlyFixedCompetitorsAndFunding()
    {
        foreach (var model in new[] { "gpt-5.6-sol", "claude-opus-4.8", "claude-sonnet-5", "gemini-3.1-pro-preview" })
            Assert.Contains(model, Sql);
        Assert.Contains("30000.00", Sql);
        Assert.Contains("120000.00", Sql);
    }

    [Fact]
    public void InitialMigrationHasDatabaseEnforcedAuditAndAccountingInvariants()
    {
        string[] required =
        [
            "canonical_jsonb_sha256", "reject_audit_mutation", "BEFORE TRUNCATE",
            "one_order_terminal_outcome", "enforce_fill_identity", "enforce_ledger_identity",
            "enforce_nonnegative_projection", "FOREIGN KEY (order_id, agent_id)",
            "FOREIGN KEY (ledger_event_id, order_id, agent_id)",
            "corporate_action_id", "pg_advisory_xact_lock", "pg_try_advisory_xact_lock",
            "FOR UPDATE SKIP LOCKED", "same idempotency key has conflicting canonical hash",
            "contest may only finish once", "rank() OVER", "apply_corporate_action",
            "observation is not the first eligible quote", "issuer_id", "is_official_pats",
            "observations traded during a pause are ineligible", "latest verified pre-decision observation is required",
            "XSTO-2026-12-30-final", "867f80011a2d8cf91f29dce6de8b6c77d4c4fda0954efa8f757f40b25c585395",
            "primary_evidence_json->>'authority' <> secondary_evidence_json->>'authority'"
        ];
        foreach (var fragment in required)
            Assert.Contains(fragment, Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeCannotBypassFunctionsOrAuditGuards()
    {
        Assert.Contains("REVOKE ALL ON SCHEMA public FROM ai_stocks_runtime", Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GRANT EXECUTE ON FUNCTION submit_order", Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT INSERT, UPDATE, DELETE ON ALL TABLES", Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanonicalJsonIsStableAcrossPropertyOrder()
    {
        using var first = JsonDocument.Parse("{\"b\":2,\"a\":1,\"nested\":{\"z\":false,\"x\":null}}");
        using var second = JsonDocument.Parse("{\"nested\":{\"x\":null,\"z\":false},\"a\":1,\"b\":2}");

        Assert.Equal(CanonicalJson.Serialize(first.RootElement), CanonicalJson.Serialize(second.RootElement));
        Assert.Equal(CanonicalJson.Sha256(first.RootElement), CanonicalJson.Sha256(second.RootElement));
        Assert.Equal(64, CanonicalJson.Sha256(first.RootElement).Length);
    }

    [Fact]
    public void MigrationChecksumIsStableAndSha256()
    {
        Assert.Equal(3, MigrationCatalog.All.Count);
        foreach (var migration in MigrationCatalog.All)
        {
            Assert.Matches("^[0-9a-f]{64}$", migration.Sha256);
            Assert.Equal(migration.Sha256, MigrationCatalog.ComputeSha256(migration.Sql));
        }
    }

    [Fact]
    public void ResearchAttestationMigrationIsImmutableHashedAndTransactionallyPersistable()
    {
        var sql = MigrationCatalog.All.Single(migration => migration.Id == "003_research_attestations").Sql;
        Assert.Contains("CREATE TABLE research_attestations", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtime_report_hash", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requested_model_id = actual_model_id", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reject_audit_mutation", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("persist_research_attestation", sql, StringComparison.OrdinalIgnoreCase);
    }
}
