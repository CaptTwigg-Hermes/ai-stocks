using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiStocks.MarketData;
using Npgsql;
using NpgsqlTypes;

namespace AiStocks.Collector;

public sealed class PostgresCollectorPersistence(
    string connectionString,
    ImmutableArchive archive,
    SessionManifestStore manifests,
    DurableFirdsStore firds,
    NasdaqStatusMachine statuses,
    string statusSeedPayloadPath,
    string statusSeedSignaturePath)
{
    public async Task PollStartedAsync(DateTimeOffset at, CancellationToken cancellationToken) =>
        await UpdateStateAsync("last_poll_started_at=$1,last_error=NULL", at, null, cancellationToken).ConfigureAwait(false);

    public async Task PollFailedAsync(DateTimeOffset at, Exception error, CancellationToken cancellationToken) =>
        await UpdateStateAsync("last_poll_started_at=$1,last_error=$2", at, error.Message, cancellationToken).ConfigureAwait(false);

    public async Task PersistAsync(CollectionResult result, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var snapshot = firds.LoadVerified();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PersistFirdsAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
        await PersistStatusAsync(connection, transaction, snapshot.Instruments, cancellationToken).ConfigureAwait(false);
        string? finalizedSession = null;
        foreach (var manifestPath in result.FinalizedManifests)
        {
            var id = Path.GetFileNameWithoutExtension(manifestPath);
            if (!id.StartsWith("XSTO-", StringComparison.Ordinal) || !DateOnly.TryParseExact(id[5..], "yyyy-MM-dd", out var day))
                throw new MarketDataException("Finalized manifest path has invalid session identity");
            var session = StockholmCalendar.GetSession(day) ?? throw new MarketDataException("Finalized manifest is not an XSTO session");
            await PersistSessionAsync(connection, transaction, session, snapshot.Instruments, cancellationToken).ConfigureAwait(false);
            finalizedSession = id;
        }
        await using var state = new NpgsqlCommand("UPDATE collector_runtime_state SET last_poll_started_at=$1,last_poll_succeeded_at=$1,last_error=NULL,last_finalized_session_id=COALESCE($2,last_finalized_session_id) WHERE singleton", connection, transaction);
        state.Parameters.AddWithValue(at.ToUniversalTime());
        state.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = finalizedSession is null ? DBNull.Value : finalizedSession });
        await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistFirdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, FirdsSnapshot snapshot, CancellationToken cancellationToken)
    {
        foreach (var version in snapshot.Versions)
        {
            await using var artifact = new NpgsqlCommand("""
                INSERT INTO market_firds_artifacts(cursor,version,is_full,source_url,payload,payload_hash,applied_at)
                VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT (cursor) DO NOTHING
                """, connection, transaction);
            artifact.Parameters.AddWithValue(version.Cursor); artifact.Parameters.AddWithValue(version.Version);
            artifact.Parameters.AddWithValue(version.IsFull); artifact.Parameters.AddWithValue(version.SourceUrl.AbsoluteUri);
            artifact.Parameters.AddWithValue(File.ReadAllBytes(version.RawPath)); artifact.Parameters.AddWithValue(version.Sha256);
            artifact.Parameters.AddWithValue(version.AppliedAt);
            await artifact.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var item in snapshot.Instruments)
        {
            var id = StableGuid($"instrument:{item.Isin}:{item.OrderBookId}:XSTO");
            var sourceJson = JsonSerializer.Serialize(new { item.Isin, item.OrderBookId, item.IssuerId, item.Name, item.Cfi, item.Currency, item.Venue, cursor = snapshot.Cursor });
            await using var instrument = new NpgsqlCommand("""
                INSERT INTO instruments(id,isin,issuer_id,order_book_id,mic,symbol,cfi,active_from,active_to,source_json,source_hash)
                VALUES($1,$2,$3,$4,'XSTO',$5,$6,COALESCE($7,current_date),$8,$9::jsonb,canonical_jsonb_sha256($9::jsonb))
                ON CONFLICT (isin,order_book_id,mic) DO NOTHING
                """, connection, transaction);
            instrument.Parameters.AddWithValue(id); instrument.Parameters.AddWithValue(item.Isin); instrument.Parameters.AddWithValue(item.IssuerId);
            instrument.Parameters.AddWithValue(item.OrderBookId); instrument.Parameters.AddWithValue(item.Name); instrument.Parameters.AddWithValue(item.Cfi);
            instrument.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = item.FirstTradeDate is null ? DBNull.Value : item.FirstTradeDate.Value });
            instrument.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = item.TerminationDate is null ? DBNull.Value : item.TerminationDate.Value });
            instrument.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = sourceJson });
            await instrument.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var version = new NpgsqlCommand("""
                INSERT INTO market_instrument_versions(firds_cursor,isin,order_book_id,issuer_id,name,cfi,currency,venue,first_trade_date,termination_date)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) ON CONFLICT DO NOTHING
                """, connection, transaction);
            version.Parameters.AddWithValue(snapshot.Cursor); version.Parameters.AddWithValue(item.Isin); version.Parameters.AddWithValue(item.OrderBookId);
            version.Parameters.AddWithValue(item.IssuerId); version.Parameters.AddWithValue(item.Name); version.Parameters.AddWithValue(item.Cfi);
            version.Parameters.AddWithValue(item.Currency); version.Parameters.AddWithValue(item.Venue);
            version.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = item.FirstTradeDate is null ? DBNull.Value : item.FirstTradeDate.Value });
            version.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = item.TerminationDate is null ? DBNull.Value : item.TerminationDate.Value });
            await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistStatusAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<FirdsInstrument> instruments, CancellationToken cancellationToken)
    {
        var payload = await File.ReadAllBytesAsync(statusSeedPayloadPath, cancellationToken).ConfigureAwait(false);
        var signature = await File.ReadAllBytesAsync(statusSeedSignaturePath, cancellationToken).ConfigureAwait(false);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO market_status_snapshots(seed_as_of,signer_key_id,signer_key_sha256,payload,payload_hash,signature)
            VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            seed.Parameters.AddWithValue(statuses.SeedAsOf); seed.Parameters.AddWithValue(statuses.SignerKeyId);
            seed.Parameters.AddWithValue(statuses.SignerKeySha256); seed.Parameters.AddWithValue(payload);
            seed.Parameters.AddWithValue(payloadHash); seed.Parameters.AddWithValue(signature);
            await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var instrument in instruments)
        {
            var stateValue = statuses.StateOf(instrument.Isin);
            if (stateValue == InstrumentTradingState.Unknown) continue;
            await using var current = new NpgsqlCommand("""
                INSERT INTO market_status_current(seed_as_of,isin,state,effective_at) VALUES($1,$2,$3,$4) ON CONFLICT DO NOTHING
                """, connection, transaction);
            current.Parameters.AddWithValue(statuses.SeedAsOf); current.Parameters.AddWithValue(instrument.Isin);
            current.Parameters.AddWithValue(stateValue.ToString()); current.Parameters.AddWithValue(statuses.LatestPublishedAt);
            await current.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var item in statuses.Events)
        {
            var rawHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item))));
            await using var command = new NpgsqlCommand("""
                INSERT INTO market_status_events(event_id,seed_as_of,isin,state,published_at,source_url,raw_hash)
                VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT DO NOTHING
                """, connection, transaction);
            command.Parameters.AddWithValue(item.Id); command.Parameters.AddWithValue(statuses.SeedAsOf); command.Parameters.AddWithValue(item.Isin);
            command.Parameters.AddWithValue(item.State.ToString()); command.Parameters.AddWithValue(item.PublishedAt);
            command.Parameters.AddWithValue(item.Source.AbsoluteUri); command.Parameters.AddWithValue(rawHash);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistSessionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TradingSession session, IReadOnlyList<FirdsInstrument> instruments, CancellationToken cancellationToken)
    {
        var verifiedManifest = manifests.Verify(session);
        await using (var sessionCommand = new NpgsqlCommand("""
            INSERT INTO trading_sessions(session_id,session_day,opens_at,closes_at,is_final) VALUES($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            sessionCommand.Parameters.AddWithValue(verifiedManifest.Manifest.SessionId); sessionCommand.Parameters.AddWithValue(session.Day);
            sessionCommand.Parameters.AddWithValue(session.Open.ToUniversalTime()); sessionCommand.Parameters.AddWithValue(session.Close.ToUniversalTime());
            sessionCommand.Parameters.AddWithValue(session.Day == StockholmCalendar.FinalSession2026().Day);
            await sessionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var rawIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var reportEntry in verifiedManifest.Manifest.Reports)
        {
            var report = archive.Verify(reportEntry.Report);
            var id = StableGuid("report:" + report.Report + ":" + report.Sha256);
            var metadata = JsonSerializer.Serialize(new { report.Report, report.Sha256, report.Bytes, sourceUrl = report.SourceUrl.AbsoluteUri, report.FetchedAt });
            await using var raw = new NpgsqlCommand("""
                INSERT INTO raw_market_reports(id,report_name,source_url,retrieved_at,payload,payload_hash,metadata_json,metadata_hash)
                VALUES($1,$2,$3,$4,$5,$6,$7::jsonb,canonical_jsonb_sha256($7::jsonb)) ON CONFLICT (report_name) DO NOTHING
                """, connection, transaction);
            raw.Parameters.AddWithValue(id); raw.Parameters.AddWithValue(report.Report); raw.Parameters.AddWithValue(report.SourceUrl.AbsoluteUri);
            raw.Parameters.AddWithValue(report.FetchedAt.ToUniversalTime()); raw.Parameters.AddWithValue(File.ReadAllBytes(report.CsvPath)); raw.Parameters.AddWithValue(report.Sha256);
            raw.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = metadata });
            await raw.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var lookup = new NpgsqlCommand("SELECT id,payload_hash::text FROM raw_market_reports WHERE report_name=$1", connection, transaction);
            lookup.Parameters.AddWithValue(report.Report);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(1) != report.Sha256)
                throw new MarketDataException("PostgreSQL raw report identity conflict");
            rawIds[report.Report] = reader.GetGuid(0);
        }
        await using (var manifest = new NpgsqlCommand("""
            INSERT INTO market_session_manifests(session_id,manifest_hash,finalized_at,source_listing_url,report_count,complete)
            VALUES($1,$2,$3,$4,$5,true) ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            manifest.Parameters.AddWithValue(verifiedManifest.Manifest.SessionId); manifest.Parameters.AddWithValue(verifiedManifest.Sha256);
            manifest.Parameters.AddWithValue(verifiedManifest.Manifest.FinalizedAt.ToUniversalTime()); manifest.Parameters.AddWithValue(verifiedManifest.Manifest.SourceListingUrl);
            manifest.Parameters.AddWithValue(verifiedManifest.Manifest.Reports.Count);
            await manifest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var instrumentIds = await LoadInstrumentIdsAsync(connection, transaction, instruments, cancellationToken).ConfigureAwait(false);
        var totals = instruments.ToDictionary(x => (x.Isin, x.OrderBookId), _ => (Value: 0m, Count: 0));
        for (var ordinal = 0; ordinal < verifiedManifest.Manifest.Reports.Count; ordinal++)
        {
            var entry = verifiedManifest.Manifest.Reports[ordinal];
            await using (var member = new NpgsqlCommand("""
                INSERT INTO market_manifest_reports(session_id,ordinal,raw_market_report_id,report_name,payload_hash)
                VALUES($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING
                """, connection, transaction))
            {
                member.Parameters.AddWithValue(verifiedManifest.Manifest.SessionId); member.Parameters.AddWithValue(ordinal);
                member.Parameters.AddWithValue(rawIds[entry.Report]); member.Parameters.AddWithValue(entry.Report); member.Parameters.AddWithValue(entry.Sha256);
                await member.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var report = archive.Verify(entry.Report);
            foreach (var trade in NasdaqCsvParser.Parse(File.ReadAllBytes(report.CsvPath), report.FetchedAt)
                .Where(x => x.Venue == "XSTO" && x.Currency == "SEK" && x.PriceNotation == "MONE" && session.Contains(x.ExecutedAt)))
            {
                foreach (var instrument in instruments.Where(x => x.Isin == trade.Isin))
                {
                    var key = (instrument.Isin, instrument.OrderBookId);
                    totals[key] = (totals[key].Value + trade.Price * trade.Quantity, totals[key].Count + 1);
                    await using var row = new NpgsqlCommand("""
                        INSERT INTO market_strict_trade_rows(id,session_id,raw_market_report_id,instrument_id,transaction_id,traded_at,published_at,retrieved_at,price,quantity,flags)
                        VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11) ON CONFLICT DO NOTHING
                        """, connection, transaction);
                    row.Parameters.AddWithValue(StableGuid($"trade:{entry.Sha256}:{instrument.OrderBookId}:{trade.TransactionId}"));
                    row.Parameters.AddWithValue(verifiedManifest.Manifest.SessionId); row.Parameters.AddWithValue(rawIds[entry.Report]);
                    row.Parameters.AddWithValue(instrumentIds[key]); row.Parameters.AddWithValue(trade.TransactionId);
                    row.Parameters.AddWithValue(trade.ExecutedAt.ToUniversalTime()); row.Parameters.AddWithValue(trade.PublishedAt.ToUniversalTime()); row.Parameters.AddWithValue(trade.FetchedAt.ToUniversalTime());
                    row.Parameters.AddWithValue(trade.Price); row.Parameters.AddWithValue(trade.Quantity); row.Parameters.AddWithValue(trade.Flags);
                    await row.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        foreach (var (key, total) in totals)
        {
            await using var stats = new NpgsqlCommand("""
                INSERT INTO instrument_session_stats(instrument_id,session_id,traded_value,complete) VALUES($1,$2,$3,true) ON CONFLICT DO NOTHING
                """, connection, transaction);
            stats.Parameters.AddWithValue(instrumentIds[key]); stats.Parameters.AddWithValue(verifiedManifest.Manifest.SessionId); stats.Parameters.AddWithValue(total.Value);
            await stats.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<(string Isin, string OrderBookId), Guid>> LoadInstrumentIdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<FirdsInstrument> instruments, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, string), Guid>();
        foreach (var instrument in instruments)
        {
            await using var command = new NpgsqlCommand("SELECT id FROM instruments WHERE isin=$1 AND order_book_id=$2 AND mic='XSTO'", connection, transaction);
            command.Parameters.AddWithValue(instrument.Isin); command.Parameters.AddWithValue(instrument.OrderBookId);
            result[(instrument.Isin, instrument.OrderBookId)] = (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new MarketDataException("PostgreSQL FIRDS projection is missing"));
        }
        return result;
    }

    private async Task UpdateStateAsync(string assignments, DateTimeOffset at, string? error, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"UPDATE collector_runtime_state SET {assignments} WHERE singleton", connection);
        command.Parameters.AddWithValue(at.ToUniversalTime());
        if (error is not null) command.Parameters.AddWithValue(error);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new MarketDataException("Collector durable runtime state is missing");
    }

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public sealed class PostgresCollectorReadiness(string connectionString)
{
    public async Task<ReadinessResult> EvaluateAsync(DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var expected = ExpectedSessions(DateOnly.FromDateTime(asOf.UtcDateTime));
            await using var command = new NpgsqlCommand("""
                WITH latest_firds AS (SELECT max(cursor) cursor FROM market_firds_artifacts),
                universe AS (SELECT isin,order_book_id FROM market_instrument_versions WHERE firds_cursor=(SELECT cursor FROM latest_firds)),
                expected AS (SELECT unnest($2::text[]) session_id),
                coverage AS (
                  SELECT u.isin,u.order_book_id,count(s.session_id) sessions,bool_or(s.traded_value > 0) has_trade
                  FROM universe u CROSS JOIN expected e
                  LEFT JOIN instruments i ON i.isin=u.isin AND i.order_book_id=u.order_book_id AND i.mic='XSTO'
                  LEFT JOIN instrument_session_stats s ON s.instrument_id=i.id AND s.session_id=e.session_id AND s.complete
                  GROUP BY u.isin,u.order_book_id)
                SELECT
                  EXISTS(SELECT 1 FROM collector_runtime_state WHERE last_error IS NULL AND last_poll_succeeded_at >= $1 - interval '2 minutes'),
                  EXISTS(SELECT 1 FROM universe),
                  EXISTS(SELECT 1 FROM market_status_snapshots) AND
                    COALESCE((SELECT max(effective_at) FROM market_status_current),'-infinity'::timestamptz) >= $1 - interval '48 hours',
                  (SELECT count(*) FROM market_session_manifests m JOIN expected e USING(session_id)
                    WHERE m.complete AND m.report_count=(SELECT count(*) FROM market_manifest_reports r WHERE r.session_id=m.session_id)) = 20,
                  NOT EXISTS(SELECT 1 FROM coverage WHERE sessions <> 20 OR NOT has_trade)
                """, connection);
            command.Parameters.AddWithValue(asOf.ToUniversalTime()); command.Parameters.AddWithValue(expected.Select(x => $"XSTO-{x:yyyy-MM-dd}").ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new(false, ["PostgreSQL readiness query returned no row"]);
            string[] messages = ["collector poll is stale or failed", "FIRDS universe is empty", "status provenance is stale", "20 complete consecutive manifests are missing", "instrument zero-day/session coverage is incomplete"];
            var failures = Enumerable.Range(0, messages.Length).Where(index => !reader.GetBoolean(index)).Select(index => messages[index]).ToArray();
            return new(failures.Length == 0, failures);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            return new(false, ["authoritative PostgreSQL unavailable: " + exception.Message]);
        }
    }

    private static IReadOnlyList<DateOnly> ExpectedSessions(DateOnly asOf)
    {
        var result = new List<DateOnly>();
        for (var day = asOf; result.Count < 20; day = day.AddDays(-1)) if (StockholmCalendar.GetSession(day) is not null) result.Add(day);
        result.Reverse(); return result;
    }
}
