-- Collector-owned authoritative market-data runtime.
DO $roles$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ai_stocks_collector') THEN
        CREATE ROLE ai_stocks_collector NOLOGIN NOINHERIT;
    END IF;
END
$roles$;

SET LOCAL ROLE ai_stocks_migrator;

CREATE TABLE market_firds_artifacts (
    cursor bigint PRIMARY KEY CHECK (cursor > 0),
    version text NOT NULL UNIQUE,
    is_full boolean NOT NULL,
    source_url text NOT NULL CHECK (source_url ~ '^https://registers\.esma\.europa\.eu/'),
    payload bytea NOT NULL CHECK (octet_length(payload) > 0),
    payload_hash sha256_hex NOT NULL CHECK (payload_hash = encode(digest(payload, 'sha256'), 'hex')::sha256_hex),
    applied_at timestamptz NOT NULL,
    UNIQUE (payload_hash, version)
);

CREATE TABLE market_instrument_versions (
    firds_cursor bigint NOT NULL REFERENCES market_firds_artifacts(cursor) ON DELETE RESTRICT,
    isin char(12) NOT NULL,
    order_book_id text NOT NULL,
    issuer_id char(20) NOT NULL,
    name text NOT NULL,
    cfi char(6) NOT NULL,
    currency char(3) NOT NULL CHECK (currency = 'SEK'),
    venue char(4) NOT NULL CHECK (venue = 'XSTO'),
    first_trade_date date,
    termination_date date,
    PRIMARY KEY (firds_cursor, isin, order_book_id)
);

CREATE TABLE market_status_snapshots (
    seed_as_of timestamptz PRIMARY KEY,
    signer_key_id text NOT NULL,
    signer_key_sha256 sha256_hex NOT NULL,
    payload bytea NOT NULL,
    payload_hash sha256_hex NOT NULL CHECK (payload_hash = encode(digest(payload, 'sha256'), 'hex')::sha256_hex),
    signature bytea NOT NULL CHECK (octet_length(signature) > 0)
);

CREATE TABLE market_status_events (
    event_id text PRIMARY KEY,
    seed_as_of timestamptz NOT NULL REFERENCES market_status_snapshots(seed_as_of) ON DELETE RESTRICT,
    isin char(12) NOT NULL,
    state text NOT NULL CHECK (state IN ('Clear','Warning','Observation','Suspended')),
    published_at timestamptz NOT NULL,
    source_url text NOT NULL CHECK (source_url ~ '^https://api\.news\.eu\.nasdaq\.com/'),
    raw_hash sha256_hex NOT NULL
);

CREATE TABLE market_status_current (
    seed_as_of timestamptz NOT NULL REFERENCES market_status_snapshots(seed_as_of) ON DELETE RESTRICT,
    isin char(12) NOT NULL,
    state text NOT NULL CHECK (state IN ('Clear','Warning','Observation','Suspended')),
    effective_at timestamptz NOT NULL,
    PRIMARY KEY (seed_as_of, isin)
);

CREATE TABLE market_session_manifests (
    session_id text PRIMARY KEY REFERENCES trading_sessions(session_id) ON DELETE RESTRICT,
    manifest_hash sha256_hex NOT NULL UNIQUE,
    finalized_at timestamptz NOT NULL,
    source_listing_url text NOT NULL CHECK (source_listing_url = 'https://tradereports.nasdaq.com/api/regulatory/trade-reports?type=POST_TRADE&assetClass=EQUITY'),
    report_count integer NOT NULL CHECK (report_count > 0),
    complete boolean NOT NULL CHECK (complete)
);

CREATE TABLE market_manifest_reports (
    session_id text NOT NULL REFERENCES market_session_manifests(session_id) ON DELETE RESTRICT,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    raw_market_report_id uuid NOT NULL REFERENCES raw_market_reports(id) ON DELETE RESTRICT,
    report_name text NOT NULL,
    payload_hash sha256_hex NOT NULL,
    PRIMARY KEY (session_id, ordinal),
    UNIQUE (session_id, report_name)
);

CREATE TABLE market_strict_trade_rows (
    id uuid PRIMARY KEY,
    session_id text NOT NULL REFERENCES market_session_manifests(session_id) ON DELETE RESTRICT,
    raw_market_report_id uuid NOT NULL REFERENCES raw_market_reports(id) ON DELETE RESTRICT,
    instrument_id uuid NOT NULL REFERENCES instruments(id) ON DELETE RESTRICT,
    transaction_id text NOT NULL,
    traded_at timestamptz NOT NULL,
    published_at timestamptz NOT NULL CHECK (published_at >= traded_at + interval '15 minutes' AND published_at <= traded_at + interval '20 minutes'),
    retrieved_at timestamptz NOT NULL CHECK (retrieved_at >= published_at),
    price numeric(20,6) NOT NULL CHECK (price > 0),
    quantity bigint NOT NULL CHECK (quantity > 0),
    flags text NOT NULL,
    is_official_pats boolean GENERATED ALWAYS AS (regexp_split_to_array(flags, '[, ;]+') @> ARRAY['PATS']) STORED,
    UNIQUE (raw_market_report_id, transaction_id)
);

CREATE TABLE collector_runtime_state (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    last_poll_started_at timestamptz,
    last_poll_succeeded_at timestamptz,
    last_error text,
    last_finalized_session_id text REFERENCES market_session_manifests(session_id) ON DELETE RESTRICT,
    CHECK (last_poll_succeeded_at IS NULL OR last_poll_started_at IS NULL OR last_poll_succeeded_at >= last_poll_started_at)
);
INSERT INTO collector_runtime_state(singleton) VALUES (true);

CREATE TRIGGER market_firds_artifacts_immutable BEFORE UPDATE OR DELETE ON market_firds_artifacts FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER market_instrument_versions_immutable BEFORE UPDATE OR DELETE ON market_instrument_versions FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER market_status_snapshots_immutable BEFORE UPDATE OR DELETE ON market_status_snapshots FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER market_status_events_immutable BEFORE UPDATE OR DELETE ON market_status_events FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER market_status_current_immutable BEFORE UPDATE OR DELETE ON market_status_current FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER market_session_manifests_immutable BEFORE UPDATE OR DELETE ON market_session_manifests FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER market_manifest_reports_immutable BEFORE UPDATE OR DELETE ON market_manifest_reports FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER market_strict_trade_rows_immutable BEFORE UPDATE OR DELETE ON market_strict_trade_rows FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();

REVOKE INSERT ON instruments,trading_sessions,instrument_session_stats,raw_market_reports,market_observations FROM ai_stocks_runtime;
REVOKE ALL ON market_firds_artifacts,market_instrument_versions,market_status_snapshots,market_status_events,market_status_current,market_session_manifests,market_manifest_reports,market_strict_trade_rows,collector_runtime_state FROM PUBLIC,ai_stocks_runtime,ai_stocks_collector;
GRANT USAGE ON SCHEMA public TO ai_stocks_collector;
GRANT SELECT ON market_firds_artifacts,market_instrument_versions,market_status_snapshots,market_status_events,market_status_current,market_session_manifests,market_manifest_reports,market_strict_trade_rows,collector_runtime_state TO ai_stocks_runtime,ai_stocks_collector;
GRANT SELECT,INSERT ON instruments,trading_sessions,instrument_session_stats,raw_market_reports,market_observations,market_firds_artifacts,market_instrument_versions,market_status_snapshots,market_status_events,market_status_current,market_session_manifests,market_manifest_reports,market_strict_trade_rows TO ai_stocks_collector;
GRANT UPDATE (last_poll_started_at,last_poll_succeeded_at,last_error,last_finalized_session_id) ON collector_runtime_state TO ai_stocks_collector;
ALTER DEFAULT PRIVILEGES FOR ROLE ai_stocks_migrator IN SCHEMA public REVOKE ALL ON TABLES FROM ai_stocks_collector;
RESET ROLE;
