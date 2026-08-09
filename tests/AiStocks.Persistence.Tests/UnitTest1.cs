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
    public void ProductionCompositionValidatesPreprovisionedMembershipsWithoutManagingClusterRoles()
    {
        var composition = MigrationCatalog.All.Single(migration => migration.Id == "006_production_composition").Sql;
        Assert.DoesNotContain("REVOKE ai_stocks_runtime FROM ai_stocks_worker", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT ai_stocks_worker_runtime TO ai_stocks_worker", composition, StringComparison.Ordinal);
        Assert.Contains("pg_has_role('ai_stocks_worker','ai_stocks_worker_runtime','member')", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void InitialMigrationDoesNotSelfGrantWhenConnectedAsMigrator()
    {
        var initial = MigrationCatalog.All.Single(migration => migration.Id == "001_production_schema").Sql;
        Assert.DoesNotContain("GRANT ai_stocks_migrator TO CURRENT_USER;", initial, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CURRENT_USER <> 'ai_stocks_migrator'", initial, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(16, MigrationCatalog.All.Count);
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

    [Fact]
    public void MarketRuntimeMigrationUsesDistinctLeastPrivilegeCollectorAuthority()
    {
        var sql = MigrationCatalog.All.Single(migration => migration.Id == "004_market_runtime").Sql;
        Assert.Contains("ai_stocks_collector", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("market_session_manifests", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("market_manifest_reports", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("market_instrument_versions", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("market_status_events", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("market_status_rss_artifacts", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("collector_runtime_state", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REVOKE INSERT ON instruments,trading_sessions,instrument_session_stats,raw_market_reports,market_observations FROM ai_stocks_runtime", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT UPDATE ON market_session_manifests", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectorPollStateAllowsAStartedPollAfterThePreviousSuccess()
    {
        var sql = MigrationCatalog.All.Single(migration => migration.Id == "011_collector_poll_state").Sql;
        Assert.Contains("DROP CONSTRAINT IF EXISTS collector_runtime_state_check", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StrictTradeTimingUsesObservedFeedDelayRatherThanPublicationFieldDelay()
    {
        var sql = MigrationCatalog.All.Single(migration => migration.Id == "012_strict_trade_timing").Sql;
        Assert.Contains("retrieved_at >= traded_at + interval '15 minutes'", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperationsPreflightCanReadMigrationHistory()
    {
        var sql = MigrationCatalog.All.Single(migration => migration.Id == "013_operations_preflight_privileges").Sql;
        Assert.Contains("GRANT SELECT ON TABLE schema_migrations", sql, StringComparison.OrdinalIgnoreCase);
        var marketSql = MigrationCatalog.All.Single(migration => migration.Id == "014_operations_market_preflight_privileges").Sql;
        Assert.Contains("GRANT SELECT ON TABLE market_session_manifests", marketSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectorNormalizesDatabaseMoneyBeforeIdempotentComparison()
    {
        Assert.Equal(1065.92m, AiStocks.Collector.PostgresCollectorPersistence.NormalizeDatabaseMoney(1065.915m));
    }

    [Fact]
    public void CollectorNormalizesDatabaseTimestampsToUtcMicroseconds()
    {
        var source = new DateTimeOffset(2026, 8, 9, 13, 53, 30, TimeSpan.FromHours(2)).AddTicks(1_654_245);
        var expected = new DateTimeOffset(2026, 8, 9, 11, 53, 30, TimeSpan.Zero).AddTicks(1_654_240);
        Assert.Equal(expected, AiStocks.Collector.PostgresCollectorPersistence.NormalizeDatabaseTimestamp(source));

        var aligned = new DateTimeOffset(2026, 8, 9, 11, 53, 30, TimeSpan.Zero).AddTicks(1_654_240);
        Assert.Equal(aligned, AiStocks.Collector.PostgresCollectorPersistence.NormalizeDatabaseTimestamp(aligned));
    }

    [Fact]
    public void StrictTradeTimingCleanupHandlesGeneratedConstraintNames()
    {
        var sql = MigrationCatalog.All.Single(migration => migration.Id == "015_strict_trade_timing_constraint_cleanup").Sql;
        Assert.Contains("market_strict_trade_rows_published_at_check", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebDashboardHasReadOnlyAccessToEveryQueriedProjection()
    {
        var sql = MigrationCatalog.All.Single(migration => migration.Id == "016_web_dashboard_privileges").Sql;
        Assert.Contains("market_observations", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scheduled_agent_runs", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostRunAcceptanceRejectsResultsCompletedAfterImmutableDeadline()
    {
        var deadline = DateTimeOffset.Parse("2026-08-08T09:15:00Z");
        var run = new AiStocks.Worker.Orchestration.RunWindow(
            "deadline", Guid.Parse("11111111-1111-1111-1111-111111111111"), "gpt-5.6-sol", 0,
            deadline.AddMinutes(-15), deadline);

        var exception = Assert.Throws<AiStocks.Research.Decisions.DecisionValidationException>(() =>
            AiStocks.Worker.PostRunAcceptance.EnsureWithinDeadline(run, deadline.AddTicks(1)));

        Assert.Contains("deadline", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
