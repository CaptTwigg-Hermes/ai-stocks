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
    string? statusSeedPayloadPath,
    string? statusSeedSignaturePath)
{
    public async Task PollStartedAsync(DateTimeOffset at, CancellationToken cancellationToken) =>
        await UpdateStateAsync("last_poll_started_at=$1,last_error=NULL", at, null, cancellationToken).ConfigureAwait(false);

    public async Task PollFailedAsync(DateTimeOffset at, Exception error, CancellationToken cancellationToken)
    {
        await UpdateStateAsync("last_poll_started_at=$1,last_error=$2", at, error.Message, cancellationToken).ConfigureAwait(false);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var alert = new NpgsqlCommand(
            "SELECT enqueue_immediate_alert('RunWideInvalidMarketData',$1,$2,$3)", connection);
        alert.Parameters.AddWithValue($"collector poll failed closed ({error.GetType().Name})");
        alert.Parameters.AddWithValue($"market-data:{at.ToUnixTimeSeconds()}");
        alert.Parameters.AddWithValue(at.ToUniversalTime());
        await alert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

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
            var appliedAt = NormalizeDatabaseTimestamp(version.AppliedAt);
            await using var artifact = new NpgsqlCommand("""
                INSERT INTO market_firds_artifacts(cursor,version,is_full,source_url,payload,payload_hash,applied_at)
                VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT (cursor) DO NOTHING
                """, connection, transaction);
            artifact.Parameters.AddWithValue(version.Cursor); artifact.Parameters.AddWithValue(version.Version);
            artifact.Parameters.AddWithValue(version.IsFull); artifact.Parameters.AddWithValue(version.SourceUrl.AbsoluteUri);
            artifact.Parameters.AddWithValue(File.ReadAllBytes(version.RawPath)); artifact.Parameters.AddWithValue(version.Sha256);
            artifact.Parameters.AddWithValue(appliedAt);
            await artifact.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await RequireMatchAsync(connection, transaction, """
                SELECT version=$2 AND is_full=$3 AND source_url=$4 AND payload_hash=$5
                FROM market_firds_artifacts WHERE cursor=$1
                """, "PostgreSQL FIRDS artifact identity conflict", cancellationToken, version.Cursor, version.Version,
                version.IsFull, version.SourceUrl.AbsoluteUri, version.Sha256).ConfigureAwait(false);
        }
        foreach (var item in snapshot.Instruments)
        {
            var id = StableGuid($"instrument:{item.Isin}:{item.OrderBookId}:XSTO");
            var sourceJson = JsonSerializer.Serialize(new { item.Isin, item.OrderBookId, item.IssuerId, item.Name, item.Cfi, item.Currency, item.Venue, cursor = snapshot.Cursor });
            await using var instrument = new NpgsqlCommand("""
                INSERT INTO instruments(id,isin,issuer_id,order_book_id,mic,symbol,cfi,active_from,active_to,source_json,source_hash)
                VALUES($1,$2,$3,$4,'XSTO',$5,$6,COALESCE($7,date '1900-01-01'),$8,$9::jsonb,canonical_jsonb_sha256($9::jsonb))
                ON CONFLICT (isin,order_book_id,mic) DO NOTHING
                """, connection, transaction);
            instrument.Parameters.AddWithValue(id); instrument.Parameters.AddWithValue(item.Isin); instrument.Parameters.AddWithValue(item.IssuerId);
            instrument.Parameters.AddWithValue(item.OrderBookId); instrument.Parameters.AddWithValue(item.Name); instrument.Parameters.AddWithValue(item.Cfi);
            instrument.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = item.FirstTradeDate is null ? DBNull.Value : item.FirstTradeDate.Value });
            instrument.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = item.TerminationDate is null ? DBNull.Value : item.TerminationDate.Value });
            instrument.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = sourceJson });
            await instrument.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await RequireMatchAsync(connection, transaction, """
                SELECT id=$1 AND issuer_id=$3 AND symbol=$5 AND cfi=$6
                  AND active_from = COALESCE(NULLIF($7,'')::date,date '1900-01-01')
                  AND active_to IS NOT DISTINCT FROM NULLIF($8,'')::date AND source_json=$9::jsonb
                FROM instruments WHERE isin=$2 AND order_book_id=$4 AND mic='XSTO'
                """, "PostgreSQL instrument identity conflict", cancellationToken, id, item.Isin, item.IssuerId,
                item.OrderBookId, item.Name, item.Cfi, item.FirstTradeDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                item.TerminationDate?.ToString("yyyy-MM-dd") ?? string.Empty, sourceJson).ConfigureAwait(false);
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
            await RequireMatchAsync(connection, transaction, """
                SELECT issuer_id=$4 AND name=$5 AND cfi=$6 AND currency=$7 AND venue=$8
                  AND first_trade_date IS NOT DISTINCT FROM NULLIF($9,'')::date
                  AND termination_date IS NOT DISTINCT FROM NULLIF($10,'')::date
                FROM market_instrument_versions WHERE firds_cursor=$1 AND isin=$2 AND order_book_id=$3
                """, "PostgreSQL FIRDS instrument version identity conflict", cancellationToken, snapshot.Cursor,
                item.Isin, item.OrderBookId, item.IssuerId, item.Name, item.Cfi, item.Currency, item.Venue,
                item.FirstTradeDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                item.TerminationDate?.ToString("yyyy-MM-dd") ?? string.Empty).ConfigureAwait(false);
        }
    }

    private async Task PersistStatusAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<FirdsInstrument> instruments, CancellationToken cancellationToken)
    {
        byte[] payload;
        byte[] signature;
        if (statusSeedPayloadPath is null && statusSeedSignaturePath is null && statuses.SignerKeyId == "public-rss-best-effort")
        {
            payload = statuses.BestEffortBootstrapPayload();
            signature = Encoding.UTF8.GetBytes("UNSIGNED-PAPER-ONLY-PUBLIC-RSS");
        }
        else if (statusSeedPayloadPath is not null && statusSeedSignaturePath is not null)
        {
            payload = await File.ReadAllBytesAsync(statusSeedPayloadPath, cancellationToken).ConfigureAwait(false);
            signature = await File.ReadAllBytesAsync(statusSeedSignaturePath, cancellationToken).ConfigureAwait(false);
        }
        else throw new MarketDataException("Status authority configuration is incomplete");
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
        await RequireMatchAsync(connection, transaction, """
            SELECT signer_key_id=$2 AND signer_key_sha256=$3 AND payload_hash=$4 AND signature=$5
            FROM market_status_snapshots WHERE seed_as_of=$1
            """, "PostgreSQL status seed identity conflict", cancellationToken, statuses.SeedAsOf,
            statuses.SignerKeyId, statuses.SignerKeySha256, payloadHash, signature).ConfigureAwait(false);
        foreach (var artifact in statuses.RssArtifacts)
        {
            var bytes = await File.ReadAllBytesAsync(artifact.RawPath, cancellationToken).ConfigureAwait(false);
            await using var rss = new NpgsqlCommand("""
                INSERT INTO market_status_rss_artifacts(payload_hash,source_url,retrieved_at,payload)
                VALUES($1,$2,$3,$4) ON CONFLICT DO NOTHING
                """, connection, transaction);
            rss.Parameters.AddWithValue(artifact.Sha256); rss.Parameters.AddWithValue(artifact.Source.AbsoluteUri);
            rss.Parameters.AddWithValue(artifact.RetrievedAt.ToUniversalTime()); rss.Parameters.AddWithValue(bytes);
            await rss.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await RequireMatchAsync(connection, transaction, """
                SELECT source_url=$2 AND payload_hash=$1
                FROM market_status_rss_artifacts WHERE payload_hash=$1 AND retrieved_at=$3
                """, "PostgreSQL status RSS identity conflict", cancellationToken, artifact.Sha256,
                artifact.Source.AbsoluteUri, artifact.RetrievedAt.ToUniversalTime()).ConfigureAwait(false);
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
            await RequireMatchAsync(connection, transaction, """
                SELECT state=$3 FROM market_status_current WHERE seed_as_of=$1 AND isin=$2 AND effective_at=$4
                """, "PostgreSQL current status identity conflict", cancellationToken, statuses.SeedAsOf,
                instrument.Isin, stateValue.ToString(), statuses.LatestPublishedAt).ConfigureAwait(false);
        }
        foreach (var item in statuses.Events)
        {
            if (item.RssSha256 is null || item.RssRetrievedAt is null)
                throw new MarketDataException("Status event lacks verified RSS artifact provenance");
            var rawHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item))));
            await using var command = new NpgsqlCommand("""
                INSERT INTO market_status_events(event_id,seed_as_of,isin,state,published_at,source_url,raw_hash,rss_payload_hash,rss_retrieved_at)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT DO NOTHING
                """, connection, transaction);
            command.Parameters.AddWithValue(item.Id); command.Parameters.AddWithValue(statuses.SeedAsOf); command.Parameters.AddWithValue(item.Isin);
            command.Parameters.AddWithValue(item.State.ToString()); command.Parameters.AddWithValue(item.PublishedAt);
            command.Parameters.AddWithValue(item.Source.AbsoluteUri); command.Parameters.AddWithValue(rawHash);
            command.Parameters.AddWithValue(item.RssSha256); command.Parameters.AddWithValue(item.RssRetrievedAt.Value.ToUniversalTime());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await RequireMatchAsync(connection, transaction, """
                SELECT seed_as_of=$2 AND isin=$3 AND state=$4 AND published_at=$5 AND source_url=$6 AND raw_hash=$7
                  AND rss_payload_hash=$8 AND rss_retrieved_at=$9
                FROM market_status_events WHERE event_id=$1
                """, "PostgreSQL status event identity conflict", cancellationToken, item.Id, statuses.SeedAsOf,
                item.Isin, item.State.ToString(), item.PublishedAt, item.Source.AbsoluteUri, rawHash,
                item.RssSha256, item.RssRetrievedAt.Value.ToUniversalTime()).ConfigureAwait(false);
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
        await RequireMatchAsync(connection, transaction, """
            SELECT session_day=$2 AND opens_at=$3 AND closes_at=$4 AND is_final=$5
            FROM trading_sessions WHERE session_id=$1
            """, "PostgreSQL trading session identity conflict", cancellationToken, verifiedManifest.Manifest.SessionId,
            session.Day, session.Open.ToUniversalTime(), session.Close.ToUniversalTime(),
            session.Day == StockholmCalendar.FinalSession2026().Day).ConfigureAwait(false);
        var rawIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var reportEntry in verifiedManifest.Manifest.Reports)
        {
            var report = archive.Verify(reportEntry.Report);
            var id = StableGuid("report:" + report.Report + ":" + report.Sha256);
            var retrievedAt = NormalizeDatabaseTimestamp(report.FetchedAt);
            var metadata = JsonSerializer.Serialize(new { report.Report, report.Sha256, report.Bytes, sourceUrl = report.SourceUrl.AbsoluteUri, report.FetchedAt });
            await using var raw = new NpgsqlCommand("""
                INSERT INTO raw_market_reports(id,report_name,source_url,retrieved_at,payload,payload_hash,metadata_json,metadata_hash)
                VALUES($1,$2,$3,$4,$5,$6,$7::jsonb,canonical_jsonb_sha256($7::jsonb)) ON CONFLICT (report_name) DO NOTHING
                """, connection, transaction);
            raw.Parameters.AddWithValue(id); raw.Parameters.AddWithValue(report.Report); raw.Parameters.AddWithValue(report.SourceUrl.AbsoluteUri);
            raw.Parameters.AddWithValue(retrievedAt); raw.Parameters.AddWithValue(File.ReadAllBytes(report.CsvPath)); raw.Parameters.AddWithValue(report.Sha256);
            raw.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = metadata });
            await raw.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var lookup = new NpgsqlCommand("SELECT id,payload_hash::text,source_url,retrieved_at=$3,metadata_json=$2::jsonb FROM raw_market_reports WHERE report_name=$1", connection, transaction);
            lookup.Parameters.AddWithValue(report.Report);
            lookup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = metadata });
            lookup.Parameters.AddWithValue(retrievedAt);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetGuid(0) != id || reader.GetString(1) != report.Sha256 ||
                reader.GetString(2) != report.SourceUrl.AbsoluteUri || !reader.GetBoolean(3) || !reader.GetBoolean(4))
                throw new MarketDataException("PostgreSQL raw report identity conflict");
            rawIds[report.Report] = reader.GetGuid(0);
        }
        var finalizedAt = NormalizeDatabaseTimestamp(verifiedManifest.Manifest.FinalizedAt);
        await using (var manifest = new NpgsqlCommand("""
            INSERT INTO market_session_manifests(session_id,manifest_hash,finalized_at,source_listing_url,report_count,complete)
            VALUES($1,$2,$3,$4,$5,true) ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            manifest.Parameters.AddWithValue(verifiedManifest.Manifest.SessionId); manifest.Parameters.AddWithValue(verifiedManifest.Sha256);
            manifest.Parameters.AddWithValue(finalizedAt); manifest.Parameters.AddWithValue(verifiedManifest.Manifest.SourceListingUrl);
            manifest.Parameters.AddWithValue(verifiedManifest.Manifest.Reports.Count);
            await manifest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await RequireMatchAsync(connection, transaction, """
            SELECT manifest_hash=$2 AND finalized_at=$3 AND source_listing_url=$4 AND report_count=$5 AND complete
            FROM market_session_manifests WHERE session_id=$1
            """, "PostgreSQL session manifest identity conflict", cancellationToken, verifiedManifest.Manifest.SessionId,
            verifiedManifest.Sha256, finalizedAt,
            verifiedManifest.Manifest.SourceListingUrl, verifiedManifest.Manifest.Reports.Count).ConfigureAwait(false);
        var instrumentIds = await LoadInstrumentIdsAsync(connection, transaction, instruments, cancellationToken).ConfigureAwait(false);
        var totals = instruments.ToDictionary(x => (x.Isin, x.OrderBookId), _ => (Value: 0m, Count: 0));
        var projectedTrades = new List<(NasdaqTrade Trade, FirdsInstrument Instrument, Guid StrictId, Guid RawId)>();
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
            await RequireMatchAsync(connection, transaction, """
                SELECT raw_market_report_id=$3 AND report_name=$4 AND payload_hash=$5
                FROM market_manifest_reports WHERE session_id=$1 AND ordinal=$2
                """, "PostgreSQL manifest member identity conflict", cancellationToken,
                verifiedManifest.Manifest.SessionId, ordinal, rawIds[entry.Report], entry.Report, entry.Sha256).ConfigureAwait(false);
            var report = archive.Verify(entry.Report);
            foreach (var trade in NasdaqCsvParser.Parse(File.ReadAllBytes(report.CsvPath), report.FetchedAt)
                .Where(x => x.Venue == "XSTO" && x.Currency == "SEK" && x.PriceNotation == "MONE" && session.Contains(x.ExecutedAt)))
            {
                var instrument = TradeInstrumentMapper.TryResolve(trade, instruments);
                if (instrument is null) continue;
                var key = (instrument.Isin, instrument.OrderBookId);
                totals[key] = (totals[key].Value + trade.Price * trade.Quantity, totals[key].Count + 1);
                var strictId = StableGuid($"trade:{entry.Report}:{entry.Sha256}:{instrument.OrderBookId}:{trade.TransactionId}");
                var tradedAt = NormalizeDatabaseTimestamp(trade.ExecutedAt);
                var publishedAt = NormalizeDatabaseTimestamp(trade.PublishedAt);
                var retrievedAt = NormalizeDatabaseTimestamp(trade.FetchedAt);
                await using var row = new NpgsqlCommand("""
                    INSERT INTO market_strict_trade_rows(id,session_id,raw_market_report_id,instrument_id,transaction_id,traded_at,published_at,retrieved_at,price,quantity,flags)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11) ON CONFLICT DO NOTHING
                    """, connection, transaction);
                row.Parameters.AddWithValue(strictId);
                row.Parameters.AddWithValue(verifiedManifest.Manifest.SessionId); row.Parameters.AddWithValue(rawIds[entry.Report]);
                row.Parameters.AddWithValue(instrumentIds[key]); row.Parameters.AddWithValue(trade.TransactionId);
                row.Parameters.AddWithValue(tradedAt); row.Parameters.AddWithValue(publishedAt); row.Parameters.AddWithValue(retrievedAt);
                row.Parameters.AddWithValue(trade.Price); row.Parameters.AddWithValue(trade.Quantity); row.Parameters.AddWithValue(trade.Flags);
                await row.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await RequireMatchAsync(connection, transaction, """
                    SELECT session_id=$2 AND raw_market_report_id=$3 AND instrument_id=$4 AND transaction_id=$5
                       AND traded_at=$6 AND published_at=$7 AND retrieved_at=$8 AND price=$9 AND quantity=$10 AND flags=$11
                    FROM market_strict_trade_rows WHERE id=$1
                    """, "PostgreSQL strict trade identity conflict", cancellationToken, strictId,
                    verifiedManifest.Manifest.SessionId, rawIds[entry.Report], instrumentIds[key], trade.TransactionId,
                    tradedAt, publishedAt, retrievedAt,
                    trade.Price, trade.Quantity, trade.Flags).ConfigureAwait(false);
                projectedTrades.Add((trade, instrument, strictId, rawIds[entry.Report]));
            }
        }
        foreach (var (key, total) in totals)
        {
            var tradedValue = decimal.Round(total.Value, 2, MidpointRounding.AwayFromZero);
            await using var stats = new NpgsqlCommand("""
                INSERT INTO instrument_session_stats(instrument_id,session_id,traded_value,complete) VALUES($1,$2,$3,true) ON CONFLICT DO NOTHING
                """, connection, transaction);
            stats.Parameters.AddWithValue(instrumentIds[key]); stats.Parameters.AddWithValue(verifiedManifest.Manifest.SessionId); stats.Parameters.AddWithValue(tradedValue);
            await stats.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await RequireMatchAsync(connection, transaction, """
                SELECT traded_value=$3 AND complete FROM instrument_session_stats WHERE instrument_id=$1 AND session_id=$2
                """, "PostgreSQL session statistics identity conflict", cancellationToken,
                instrumentIds[key], verifiedManifest.Manifest.SessionId, tradedValue).ConfigureAwait(false);
        }
        foreach (var item in projectedTrades.DistinctBy(x => x.StrictId))
        {
            var instrumentId = instrumentIds[(item.Instrument.Isin, item.Instrument.OrderBookId)];
            await using var history = new NpgsqlCommand("""
                SELECT avg(traded_value),count(*) FROM (
                  SELECT s.traded_value FROM instrument_session_stats s JOIN trading_sessions ts USING(session_id)
                  WHERE s.instrument_id=$1 AND s.complete AND ts.session_day <= $2 ORDER BY ts.session_day DESC LIMIT 20
                ) recent
                """, connection, transaction);
            history.Parameters.AddWithValue(instrumentId); history.Parameters.AddWithValue(session.Day);
            await using var historyReader = await history.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await historyReader.ReadAsync(cancellationToken).ConfigureAwait(false) || historyReader.IsDBNull(0))
                throw new MarketDataException("PostgreSQL observation history is missing");
            var averageDailyValue = NormalizeDatabaseMoney(historyReader.GetDecimal(0));
            var completeSessions = historyReader.GetInt64(1);
            await historyReader.DisposeAsync().ConfigureAwait(false);
            var state = statuses.StateAt(item.Instrument.Isin, item.Trade.ExecutedAt);
            var sourceJson = JsonSerializer.Serialize(new
            {
                strictTradeRowId = item.StrictId,
                manifestHash = verifiedManifest.Sha256,
                isin = item.Instrument.Isin,
                orderBookId = item.Instrument.OrderBookId,
                transactionId = item.Trade.TransactionId
            });
            var observationId = StableGuid("observation:" + item.StrictId);
            var tradedAt = NormalizeDatabaseTimestamp(item.Trade.ExecutedAt);
            var retrievedAt = NormalizeDatabaseTimestamp(item.Trade.FetchedAt);
            await using var observation = new NpgsqlCommand("""
                INSERT INTO market_observations(id,instrument_id,raw_market_report_id,traded_at,retrieved_at,price,quantity,
                  average_daily_value_20,complete_history_sessions,session_id,is_official_pats,warning,suspended,verified,source_json,source_hash)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15::jsonb,canonical_jsonb_sha256($15::jsonb))
                ON CONFLICT DO NOTHING
                """, connection, transaction);
            observation.Parameters.AddWithValue(observationId); observation.Parameters.AddWithValue(instrumentId); observation.Parameters.AddWithValue(item.RawId);
            observation.Parameters.AddWithValue(tradedAt); observation.Parameters.AddWithValue(retrievedAt);
            observation.Parameters.AddWithValue(item.Trade.Price); observation.Parameters.AddWithValue(item.Trade.Quantity); observation.Parameters.AddWithValue(averageDailyValue);
            observation.Parameters.AddWithValue(checked((int)completeSessions)); observation.Parameters.AddWithValue(verifiedManifest.Manifest.SessionId);
            observation.Parameters.AddWithValue(item.Trade.Flags.Split(',', ' ', ';').Contains("PATS", StringComparer.Ordinal));
            observation.Parameters.AddWithValue(state is InstrumentTradingState.Warning or InstrumentTradingState.Observation);
            observation.Parameters.AddWithValue(state == InstrumentTradingState.Suspended); observation.Parameters.AddWithValue(state == InstrumentTradingState.Clear);
            observation.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = sourceJson });
            await observation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await RequireMatchAsync(connection, transaction, """
                SELECT instrument_id=$2 AND raw_market_report_id=$3 AND traded_at=$4 AND retrieved_at=$5 AND price=$6 AND quantity=$7
                   AND average_daily_value_20=$8 AND complete_history_sessions=$9 AND session_id=$10 AND is_official_pats=$11
                   AND warning=$12 AND suspended=$13 AND verified=$14 AND source_json=$15::jsonb
                FROM market_observations WHERE id=$1
                """, "PostgreSQL market observation identity conflict", cancellationToken, observationId, instrumentId, item.RawId,
                tradedAt, retrievedAt, item.Trade.Price, item.Trade.Quantity,
                averageDailyValue, checked((int)completeSessions), verifiedManifest.Manifest.SessionId,
                item.Trade.Flags.Split(',', ' ', ';').Contains("PATS", StringComparer.Ordinal),
                state is InstrumentTradingState.Warning or InstrumentTradingState.Observation,
                state == InstrumentTradingState.Suspended, state == InstrumentTradingState.Clear, sourceJson).ConfigureAwait(false);
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

    private static async Task RequireMatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        string error, CancellationToken cancellationToken, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new MarketDataException(error);
    }

    internal static decimal NormalizeDatabaseMoney(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    internal static DateTimeOffset NormalizeDatabaseTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().AddTicks(-(value.ToUniversalTime().Ticks % TimeSpan.TicksPerMicrosecond));

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
            var expected = ExpectedSessions(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOf, StockholmCalendar.Zone).DateTime));
            await using var command = new NpgsqlCommand("""
                WITH latest_firds AS (SELECT max(cursor) cursor FROM market_firds_artifacts),
                universe AS (SELECT isin,order_book_id FROM market_instrument_versions WHERE firds_cursor=(SELECT cursor FROM latest_firds)),
                expected AS (SELECT unnest($2::text[]) session_id),
                coverage AS (
                  SELECT u.isin,u.order_book_id,count(s.session_id) sessions,bool_or(s.traded_value > 0) has_trade,
                    bool_or(EXISTS(SELECT 1 FROM market_observations o WHERE o.instrument_id=i.id AND o.verified AND NOT o.warning AND NOT o.suspended)) has_observation
                  FROM universe u CROSS JOIN expected e
                  LEFT JOIN instruments i ON i.isin=u.isin AND i.order_book_id=u.order_book_id AND i.mic='XSTO'
                  LEFT JOIN instrument_session_stats s ON s.instrument_id=i.id AND s.session_id=e.session_id AND s.complete
                  GROUP BY u.isin,u.order_book_id)
                SELECT
                  EXISTS(SELECT 1 FROM collector_runtime_state WHERE last_error IS NULL AND last_poll_succeeded_at >= $1 - interval '2 minutes'),
                  EXISTS(SELECT 1 FROM universe),
                  EXISTS(SELECT 1 FROM market_status_snapshots) AND
                    COALESCE((SELECT max(retrieved_at) FROM market_status_rss_artifacts),'-infinity'::timestamptz) >= $1 - interval '2 hours',
                  (SELECT count(*) FROM market_session_manifests m JOIN expected e USING(session_id)
                    WHERE m.complete AND m.report_count=(SELECT count(*) FROM market_manifest_reports r WHERE r.session_id=m.session_id)) = 20,
                  NOT EXISTS(SELECT 1 FROM coverage WHERE sessions <> 20 OR NOT COALESCE(has_trade,false) OR NOT COALESCE(has_observation,false))
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
