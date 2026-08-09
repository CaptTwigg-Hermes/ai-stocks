-- Production PostgreSQL schema. Applied only by ai_stocks_migrator.
-- CREATE TABLE schema_migrations is bootstrapped by PostgresMigrationRunner.
CREATE EXTENSION IF NOT EXISTS pgcrypto;

DO $roles$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ai_stocks_migrator') THEN
        CREATE ROLE ai_stocks_migrator NOLOGIN NOINHERIT;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ai_stocks_runtime') THEN
        CREATE ROLE ai_stocks_runtime NOLOGIN NOINHERIT;
    END IF;
END
$roles$;

GRANT USAGE, CREATE ON SCHEMA public TO ai_stocks_migrator;
ALTER TABLE schema_migrations OWNER TO ai_stocks_migrator;
DO $grant_migrator$
BEGIN
    IF CURRENT_USER <> 'ai_stocks_migrator' THEN
        EXECUTE format('GRANT ai_stocks_migrator TO %I', CURRENT_USER);
    END IF;
END
$grant_migrator$;
SET LOCAL ROLE ai_stocks_migrator;

CREATE DOMAIN sha256_hex AS char(64)
    CHECK (VALUE ~ '^[0-9a-f]{64}$');
CREATE DOMAIN nonnegative_money AS numeric(20,2)
    CHECK (VALUE >= 0);
CREATE TYPE contest_status AS ENUM ('DRAFT','RUNNING','PAUSED','FINISHED');
CREATE TYPE order_side AS ENUM ('BUY','SELL');
CREATE TYPE order_terminal_status AS ENUM ('FILLED','REJECTED','CANCELLED','REPLACED');
CREATE TYPE run_status AS ENUM ('PENDING','CLAIMED','SUCCEEDED','FAILED','MISSED');
CREATE TYPE fee_tier AS ENUM ('STARTER','MINI');
CREATE TYPE corporate_action_type AS ENUM ('DIVIDEND','SPLIT','CASH_MERGER','STOCK_MERGER','DELISTING','CORRECTION');

CREATE OR REPLACE FUNCTION canonical_jsonb_sha256(value jsonb) RETURNS sha256_hex
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
RETURN encode(digest(convert_to(value::text, 'UTF8'), 'sha256'), 'hex')::sha256_hex;

CREATE TABLE agents (
    id uuid PRIMARY KEY,
    model_id text NOT NULL UNIQUE CHECK (model_id IN
      ('gpt-5.6-sol','claude-opus-4.8','claude-sonnet-5','gemini-3.1-pro-preview')),
    initial_cash nonnegative_money NOT NULL CHECK (initial_cash = 30000.00),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (id, model_id)
);
INSERT INTO agents(id, model_id, initial_cash) VALUES
 ('11111111-1111-1111-1111-111111111111','gpt-5.6-sol',30000.00),
 ('22222222-2222-2222-2222-222222222222','claude-opus-4.8',30000.00),
 ('33333333-3333-3333-3333-333333333333','claude-sonnet-5',30000.00),
 ('44444444-4444-4444-4444-444444444444','gemini-3.1-pro-preview',30000.00);
-- Fixed contest funding: 4 * 30000.00 = 120000.00 SEK.

CREATE TABLE contest_state (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    status contest_status NOT NULL DEFAULT 'DRAFT',
    started_at timestamptz,
    finished_at timestamptz,
    CHECK ((status = 'DRAFT' AND started_at IS NULL AND finished_at IS NULL)
        OR (status IN ('RUNNING','PAUSED') AND started_at IS NOT NULL AND finished_at IS NULL)
        OR (status = 'FINISHED' AND started_at IS NOT NULL AND finished_at IS NOT NULL))
);
INSERT INTO contest_state(singleton, status) VALUES (true, 'DRAFT');

CREATE TABLE contest_state_events (
    id uuid PRIMARY KEY,
    idempotency_key text NOT NULL UNIQUE,
    request_json jsonb NOT NULL,
    request_hash sha256_hex NOT NULL CHECK (request_hash = canonical_jsonb_sha256(request_json)),
    from_status contest_status NOT NULL,
    to_status contest_status NOT NULL,
    reason text NOT NULL CHECK (length(reason) BETWEEN 1 AND 1000),
    actor text NOT NULL,
    occurred_at timestamptz NOT NULL
);

CREATE TABLE instruments (
    id uuid PRIMARY KEY,
    isin char(12) NOT NULL,
    issuer_id char(20) NOT NULL CHECK (issuer_id ~ '^[A-Z0-9]{20}$'),
    order_book_id text NOT NULL,
    mic char(4) NOT NULL CHECK (mic = 'XSTO'),
    symbol text NOT NULL,
    cfi char(6) NOT NULL,
    active_from date NOT NULL,
    active_to date,
    source_json jsonb NOT NULL,
    source_hash sha256_hex NOT NULL CHECK (source_hash = canonical_jsonb_sha256(source_json)),
    CHECK (active_to IS NULL OR active_to >= active_from),
    UNIQUE (isin, order_book_id, mic),
    UNIQUE (id, mic)
);

CREATE TABLE trading_sessions (
    session_id text PRIMARY KEY,
    session_day date NOT NULL UNIQUE,
    opens_at timestamptz NOT NULL,
    closes_at timestamptz NOT NULL,
    is_final boolean NOT NULL DEFAULT false,
    calendar_sha256 sha256_hex,
    trading_hours_sha256 sha256_hex,
    CHECK (closes_at > opens_at),
    CHECK (NOT is_final OR (
      session_id='XSTO-2026-12-30' AND session_day=date '2026-12-30'
      AND opens_at=timestamptz '2026-12-30 08:00:00Z'
      AND closes_at=timestamptz '2026-12-30 16:30:00Z'
      AND calendar_sha256='867f80011a2d8cf91f29dce6de8b6c77d4c4fda0954efa8f757f40b25c585395'
      AND trading_hours_sha256='f16f58c7520eaaae3210ddab666e7bde2609d1935c69e9f5d706bbd0d14fe395'))
);

CREATE TABLE instrument_session_stats (
    instrument_id uuid NOT NULL REFERENCES instruments(id) ON DELETE RESTRICT,
    session_id text NOT NULL REFERENCES trading_sessions(session_id) ON DELETE RESTRICT,
    traded_value numeric(24,2) NOT NULL CHECK (traded_value >= 0),
    complete boolean NOT NULL,
    PRIMARY KEY (instrument_id, session_id)
);

CREATE TABLE raw_market_reports (
    id uuid PRIMARY KEY,
    report_name text NOT NULL UNIQUE CHECK (length(report_name) BETWEEN 1 AND 300),
    source_url text NOT NULL CHECK (source_url ~ '^https://'),
    retrieved_at timestamptz NOT NULL,
    payload bytea NOT NULL CHECK (octet_length(payload) > 0),
    payload_hash sha256_hex NOT NULL
        CHECK (payload_hash = encode(digest(payload, 'sha256'), 'hex')::sha256_hex),
    metadata_json jsonb NOT NULL,
    metadata_hash sha256_hex NOT NULL
        CHECK (metadata_hash = canonical_jsonb_sha256(metadata_json)),
    UNIQUE (report_name, payload_hash)
);

CREATE TABLE market_observations (
    id uuid PRIMARY KEY,
    instrument_id uuid NOT NULL REFERENCES instruments(id) ON DELETE RESTRICT,
    raw_market_report_id uuid NOT NULL REFERENCES raw_market_reports(id) ON DELETE RESTRICT,
    traded_at timestamptz NOT NULL,
    retrieved_at timestamptz NOT NULL CHECK (retrieved_at >= traded_at),
    price numeric(20,6) NOT NULL CHECK (price > 0),
    quantity bigint NOT NULL CHECK (quantity > 0),
    bid numeric(20,6) CHECK (bid > 0),
    ask numeric(20,6) CHECK (ask > 0),
    average_daily_value_20 numeric(24,2) NOT NULL CHECK (average_daily_value_20 > 0),
    complete_history_sessions integer NOT NULL CHECK (complete_history_sessions >= 0),
    session_id text NOT NULL REFERENCES trading_sessions(session_id) ON DELETE RESTRICT,
    is_official_pats boolean NOT NULL DEFAULT false,
    warning boolean NOT NULL,
    suspended boolean NOT NULL,
    verified boolean NOT NULL,
    source_json jsonb NOT NULL,
    source_hash sha256_hex NOT NULL CHECK (source_hash = canonical_jsonb_sha256(source_json)),
    CHECK (ask IS NULL OR bid IS NULL OR ask >= bid),
    UNIQUE (instrument_id, traded_at, source_hash)
);

CREATE TABLE prompts (
    id uuid PRIMARY KEY,
    version text NOT NULL UNIQUE,
    prompt_json jsonb NOT NULL,
    prompt_hash sha256_hex NOT NULL UNIQUE CHECK (prompt_hash = canonical_jsonb_sha256(prompt_json)),
    created_at timestamptz NOT NULL
);

CREATE TABLE scheduled_agent_runs (
    id uuid PRIMARY KEY,
    run_key text NOT NULL UNIQUE,
    agent_id uuid NOT NULL,
    model_id text NOT NULL,
    scheduled_at timestamptz NOT NULL,
    deadline_at timestamptz NOT NULL CHECK (deadline_at >= scheduled_at),
    status run_status NOT NULL DEFAULT 'PENDING',
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    next_attempt_at timestamptz NOT NULL,
    claim_token uuid,
    lease_until timestamptz,
    last_error text,
    completed_at timestamptz,
    FOREIGN KEY (agent_id, model_id) REFERENCES agents(id, model_id) ON DELETE RESTRICT,
    CHECK ((status = 'CLAIMED') = (claim_token IS NOT NULL AND lease_until IS NOT NULL)),
    CHECK ((status IN ('SUCCEEDED','FAILED','MISSED')) = (completed_at IS NOT NULL))
);
CREATE INDEX scheduled_runs_claim_idx ON scheduled_agent_runs(next_attempt_at, scheduled_at)
 WHERE status IN ('PENDING','CLAIMED');

CREATE TABLE agent_runs (
    id uuid PRIMARY KEY,
    scheduled_run_id uuid NOT NULL REFERENCES scheduled_agent_runs(id) ON DELETE RESTRICT,
    attempt integer NOT NULL CHECK (attempt > 0),
    agent_id uuid NOT NULL,
    model_id text NOT NULL,
    prompt_id uuid NOT NULL REFERENCES prompts(id) ON DELETE RESTRICT,
    started_at timestamptz NOT NULL,
    ended_at timestamptz NOT NULL CHECK (ended_at >= started_at),
    status run_status NOT NULL CHECK (status IN ('SUCCEEDED','FAILED','MISSED')),
    audit_json jsonb NOT NULL,
    audit_hash sha256_hex NOT NULL CHECK (audit_hash = canonical_jsonb_sha256(audit_json)),
    FOREIGN KEY (agent_id, model_id) REFERENCES agents(id, model_id) ON DELETE RESTRICT,
    UNIQUE (scheduled_run_id, attempt)
);

CREATE TABLE strategies (
    id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
    version integer NOT NULL CHECK (version > 0),
    strategy_json jsonb NOT NULL,
    strategy_hash sha256_hex NOT NULL CHECK (strategy_hash = canonical_jsonb_sha256(strategy_json)),
    created_at timestamptz NOT NULL,
    UNIQUE (agent_id, version)
);

CREATE TABLE orders (
    id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
    decision_id text NOT NULL,
    idempotency_key text NOT NULL,
    side order_side NOT NULL,
    instrument_id uuid NOT NULL REFERENCES instruments(id) ON DELETE RESTRICT,
    quantity integer NOT NULL CHECK (quantity > 0),
    decision_at timestamptz NOT NULL,
    observed_price numeric(20,6) NOT NULL CHECK (observed_price > 0),
    request_json jsonb NOT NULL,
    request_hash sha256_hex NOT NULL CHECK (request_hash = canonical_jsonb_sha256(request_json)),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (agent_id, decision_id),
    UNIQUE (agent_id, idempotency_key),
    UNIQUE (id, agent_id)
);

CREATE TABLE order_outcomes (
    id uuid PRIMARY KEY,
    order_id uuid NOT NULL,
    agent_id uuid NOT NULL,
    status order_terminal_status NOT NULL,
    idempotency_key text NOT NULL,
    outcome_json jsonb NOT NULL,
    outcome_hash sha256_hex NOT NULL CHECK (outcome_hash = canonical_jsonb_sha256(outcome_json)),
    occurred_at timestamptz NOT NULL,
    FOREIGN KEY (order_id, agent_id) REFERENCES orders(id, agent_id) ON DELETE RESTRICT,
    CONSTRAINT one_order_terminal_outcome UNIQUE (order_id),
    UNIQUE (agent_id, idempotency_key),
    UNIQUE (id, order_id, agent_id)
);

CREATE TABLE corporate_actions (
    id uuid PRIMARY KEY,
    external_reference text NOT NULL UNIQUE,
    instrument_id uuid NOT NULL REFERENCES instruments(id) ON DELETE RESTRICT,
    action_type corporate_action_type NOT NULL,
    effective_at timestamptz NOT NULL,
    normalized_json jsonb NOT NULL,
    normalized_hash sha256_hex NOT NULL CHECK (normalized_hash = canonical_jsonb_sha256(normalized_json)),
    primary_evidence_json jsonb NOT NULL,
    primary_evidence_hash sha256_hex NOT NULL CHECK (primary_evidence_hash = canonical_jsonb_sha256(primary_evidence_json)),
    secondary_evidence_json jsonb NOT NULL,
    secondary_evidence_hash sha256_hex NOT NULL CHECK (secondary_evidence_hash = canonical_jsonb_sha256(secondary_evidence_json)),
    approved_by text NOT NULL CHECK (length(approved_by) BETWEEN 1 AND 300),
    CHECK (primary_evidence_json <> secondary_evidence_json),
    CHECK (length(primary_evidence_json->>'authority') > 0),
    CHECK (length(secondary_evidence_json->>'authority') > 0),
    CHECK (primary_evidence_json->>'authority' <> secondary_evidence_json->>'authority'),
    created_at timestamptz NOT NULL
);

CREATE TABLE ledger_events (
    id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
    event_type text NOT NULL CHECK (event_type IN
      ('INITIAL_FUNDING','BUY_FILL','SELL_FILL','DIVIDEND','SPLIT','CASH_MERGER','STOCK_MERGER','DELISTING','CORRECTION','FINAL_LIQUIDATION')),
    instrument_id uuid REFERENCES instruments(id) ON DELETE RESTRICT,
    order_id uuid,
    corporate_action_id uuid REFERENCES corporate_actions(id) ON DELETE RESTRICT,
    cash_delta numeric(20,2) NOT NULL,
    quantity_delta integer NOT NULL,
    average_cost_after numeric(20,4) CHECK (average_cost_after >= 0),
    unit_price numeric(20,6) CHECK (unit_price > 0),
    gross nonnegative_money,
    fee nonnegative_money,
    occurred_at timestamptz NOT NULL,
    event_json jsonb NOT NULL,
    event_hash sha256_hex NOT NULL CHECK (event_hash = canonical_jsonb_sha256(event_json)),
    FOREIGN KEY (order_id, agent_id) REFERENCES orders(id, agent_id) ON DELETE RESTRICT,
    CHECK ((event_type IN ('BUY_FILL','SELL_FILL','FINAL_LIQUIDATION')) = (order_id IS NOT NULL)),
    CHECK ((event_type IN ('DIVIDEND','SPLIT','CASH_MERGER','STOCK_MERGER','DELISTING')) = (corporate_action_id IS NOT NULL)
        OR event_type = 'CORRECTION'),
    CHECK ((event_type = 'INITIAL_FUNDING' AND instrument_id IS NULL AND cash_delta = 30000.00 AND quantity_delta = 0)
        OR event_type <> 'INITIAL_FUNDING'),
    UNIQUE (id, agent_id),
    UNIQUE (id, order_id, agent_id)
);

CREATE TABLE fills (
    id uuid PRIMARY KEY,
    order_id uuid NOT NULL,
    agent_id uuid NOT NULL,
    ledger_event_id uuid NOT NULL,
    market_observation_id uuid NOT NULL REFERENCES market_observations(id) ON DELETE RESTRICT,
    quantity integer NOT NULL CHECK (quantity > 0),
    execution_price numeric(20,6) NOT NULL CHECK (execution_price > 0),
    gross nonnegative_money NOT NULL CHECK (gross > 0),
    fee nonnegative_money NOT NULL,
    slippage_rate numeric(10,8) NOT NULL CHECK (slippage_rate BETWEEN 0.001 AND 0.01),
    executed_at timestamptz NOT NULL,
    fill_json jsonb NOT NULL,
    fill_hash sha256_hex NOT NULL CHECK (fill_hash = canonical_jsonb_sha256(fill_json)),
    FOREIGN KEY (order_id, agent_id) REFERENCES orders(id, agent_id) ON DELETE RESTRICT,
    FOREIGN KEY (ledger_event_id, order_id, agent_id) REFERENCES ledger_events(id, order_id, agent_id) ON DELETE RESTRICT,
    UNIQUE (order_id),
    UNIQUE (ledger_event_id)
);

CREATE TABLE corporate_action_applications (
    id uuid PRIMARY KEY,
    corporate_action_id uuid NOT NULL REFERENCES corporate_actions(id) ON DELETE RESTRICT,
    agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
    ledger_event_id uuid NOT NULL,
    application_json jsonb NOT NULL,
    application_hash sha256_hex NOT NULL CHECK (application_hash = canonical_jsonb_sha256(application_json)),
    applied_at timestamptz NOT NULL,
    FOREIGN KEY (ledger_event_id, agent_id) REFERENCES ledger_events(id, agent_id) ON DELETE RESTRICT,
    UNIQUE (corporate_action_id, agent_id),
    UNIQUE (ledger_event_id)
);

CREATE TABLE fractional_entitlements (
    id uuid PRIMARY KEY,
    corporate_action_id uuid NOT NULL REFERENCES corporate_actions(id) ON DELETE RESTRICT,
    agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
    instrument_id uuid NOT NULL REFERENCES instruments(id) ON DELETE RESTRICT,
    quantity numeric(20,8) NOT NULL CHECK (quantity > 0 AND quantity < 1),
    average_cost numeric(20,4) NOT NULL CHECK (average_cost >= 0),
    created_at timestamptz NOT NULL,
    UNIQUE (corporate_action_id, agent_id, instrument_id)
);

CREATE TABLE account_balances (
    agent_id uuid PRIMARY KEY REFERENCES agents(id) ON DELETE RESTRICT,
    cash nonnegative_money NOT NULL,
    fee_tier fee_tier NOT NULL DEFAULT 'STARTER',
    stock_trade_count integer NOT NULL DEFAULT 0 CHECK (stock_trade_count >= 0),
    updated_at timestamptz NOT NULL
);
CREATE TABLE positions (
    agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
    instrument_id uuid NOT NULL REFERENCES instruments(id) ON DELETE RESTRICT,
    quantity integer NOT NULL CHECK (quantity >= 0),
    average_cost numeric(20,4) NOT NULL CHECK (average_cost >= 0),
    frozen boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (agent_id, instrument_id)
);

CREATE TABLE portfolio_snapshots (
    id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
    as_of timestamptz NOT NULL,
    cash nonnegative_money NOT NULL,
    snapshot_json jsonb NOT NULL,
    snapshot_hash sha256_hex NOT NULL CHECK (snapshot_hash = canonical_jsonb_sha256(snapshot_json)),
    UNIQUE (agent_id, as_of)
);

CREATE TABLE final_rankings (
    id uuid PRIMARY KEY,
    reference text NOT NULL,
    agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
    rank integer NOT NULL CHECK (rank > 0),
    net_liquidation_value nonnegative_money NOT NULL,
    input_json jsonb NOT NULL,
    input_hash sha256_hex NOT NULL CHECK (input_hash = canonical_jsonb_sha256(input_json)),
    finalized_at timestamptz NOT NULL,
    UNIQUE (reference, agent_id)
);

CREATE OR REPLACE FUNCTION reject_audit_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% records are immutable and cannot be %', TG_TABLE_NAME, lower(TG_OP);
END $$;

DO $audit_guards$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'contest_state_events','instruments','trading_sessions','instrument_session_stats','raw_market_reports','market_observations','prompts','agent_runs','strategies',
    'orders','order_outcomes','ledger_events','fills','corporate_actions',
    'corporate_action_applications','fractional_entitlements','portfolio_snapshots','final_rankings'
  ] LOOP
    EXECUTE format('CREATE TRIGGER %I_no_update_delete BEFORE UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation()', t, t);
    EXECUTE format('CREATE TRIGGER %I_no_truncate BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation()', t, t);
  END LOOP;
END $audit_guards$;

CREATE OR REPLACE FUNCTION enforce_ledger_identity() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE order_side_value order_side;
BEGIN
  PERFORM 1 FROM agents WHERE id = NEW.agent_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'ledger agent does not exist'; END IF;
  IF NEW.order_id IS NOT NULL THEN
    SELECT side INTO STRICT order_side_value FROM orders
      WHERE id = NEW.order_id AND agent_id = NEW.agent_id;
    IF NEW.event_type = 'BUY_FILL' AND (order_side_value <> 'BUY' OR NEW.cash_delta <> -(NEW.gross + NEW.fee)) THEN
      RAISE EXCEPTION 'buy ledger arithmetic identity failed';
    ELSIF NEW.event_type IN ('SELL_FILL','FINAL_LIQUIDATION')
       AND (order_side_value <> 'SELL' OR NEW.cash_delta <> NEW.gross - NEW.fee) THEN
      RAISE EXCEPTION 'sell ledger arithmetic identity failed';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER ledger_identity BEFORE INSERT ON ledger_events
FOR EACH ROW EXECUTE FUNCTION enforce_ledger_identity();

CREATE OR REPLACE FUNCTION enforce_nonnegative_projection() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
  new_cash numeric(20,2);
  new_quantity integer;
  current_quantity integer;
  current_average numeric(20,4);
BEGIN
  UPDATE account_balances SET cash=cash+NEW.cash_delta,updated_at=NEW.occurred_at
   WHERE agent_id=NEW.agent_id RETURNING cash INTO new_cash;
  IF NOT FOUND THEN
    INSERT INTO account_balances(agent_id,cash,updated_at)
      VALUES (NEW.agent_id,NEW.cash_delta,NEW.occurred_at) RETURNING cash INTO new_cash;
  END IF;
  IF new_cash < 0 THEN RAISE EXCEPTION 'cash balance must remain nonnegative'; END IF;
  IF NEW.instrument_id IS NOT NULL AND (NEW.quantity_delta <> 0 OR NEW.average_cost_after IS NOT NULL) THEN
    SELECT quantity,average_cost INTO current_quantity,current_average FROM positions
      WHERE agent_id=NEW.agent_id AND instrument_id=NEW.instrument_id;
    IF COALESCE(current_quantity,0)+NEW.quantity_delta < 0 THEN
      RAISE EXCEPTION 'holding balance must remain nonnegative';
    END IF;
    UPDATE positions SET
      average_cost=CASE
        WHEN quantity+NEW.quantity_delta=0 THEN 0
        WHEN NEW.average_cost_after IS NOT NULL THEN NEW.average_cost_after
        WHEN NEW.event_type='BUY_FILL' THEN round((average_cost*quantity+NEW.gross+NEW.fee)/(quantity+NEW.quantity_delta),4)
        ELSE average_cost END,
      quantity=quantity+NEW.quantity_delta,updated_at=NEW.occurred_at
     WHERE agent_id=NEW.agent_id AND instrument_id=NEW.instrument_id
     RETURNING quantity INTO new_quantity;
    IF NOT FOUND THEN
      IF NEW.quantity_delta < 0 THEN RAISE EXCEPTION 'holding balance must remain nonnegative'; END IF;
      INSERT INTO positions(agent_id,instrument_id,quantity,average_cost,updated_at)
      VALUES(NEW.agent_id,NEW.instrument_id,NEW.quantity_delta,
             COALESCE(NEW.average_cost_after,
               CASE WHEN NEW.event_type='BUY_FILL' THEN round((NEW.gross+NEW.fee)/NEW.quantity_delta,4) ELSE 0 END),
             NEW.occurred_at)
      RETURNING quantity INTO new_quantity;
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER ledger_nonnegative_projection AFTER INSERT ON ledger_events
FOR EACH ROW EXECUTE FUNCTION enforce_nonnegative_projection();

CREATE OR REPLACE FUNCTION enforce_fill_identity() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE o orders%ROWTYPE; l ledger_events%ROWTYPE; observation market_observations%ROWTYPE;
BEGIN
  SELECT * INTO STRICT o FROM orders WHERE id=NEW.order_id AND agent_id=NEW.agent_id;
  SELECT * INTO STRICT l FROM ledger_events
    WHERE id=NEW.ledger_event_id AND order_id=NEW.order_id AND agent_id=NEW.agent_id;
  SELECT * INTO STRICT observation FROM market_observations WHERE id=NEW.market_observation_id;
  IF observation.instrument_id<>o.instrument_id OR observation.traded_at<o.decision_at
     OR NOT observation.verified OR observation.warning OR observation.suspended
     OR NEW.executed_at<>observation.retrieved_at
     OR NEW.quantity <> o.quantity OR NEW.gross <> round(NEW.quantity * NEW.execution_price, 2)
     OR NEW.gross <> l.gross OR NEW.fee <> l.fee OR NEW.quantity <> abs(l.quantity_delta)
     OR (o.side='BUY' AND l.event_type <> 'BUY_FILL')
     OR (o.side='SELL' AND l.event_type NOT IN ('SELL_FILL','FINAL_LIQUIDATION')) THEN
    RAISE EXCEPTION 'fill/order/ledger arithmetic identity failed';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER fill_identity BEFORE INSERT ON fills
FOR EACH ROW EXECUTE FUNCTION enforce_fill_identity();

CREATE OR REPLACE FUNCTION submit_order(
  p_id uuid, p_agent_id uuid, p_decision_id text, p_idempotency_key text,
  p_side order_side, p_instrument_id uuid, p_quantity integer, p_decision_at timestamptz,
  p_request_json jsonb, p_request_hash sha256_hex
) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE existing orders%ROWTYPE; authoritative_price numeric;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('lifecycle',0));
  PERFORM pg_advisory_xact_lock(hashtextextended(p_agent_id::text,0));
  IF (SELECT status FROM contest_state WHERE singleton FOR SHARE) <> 'RUNNING' THEN
    RAISE EXCEPTION 'contest is not running';
  END IF;
  IF p_request_hash <> canonical_jsonb_sha256(p_request_json) THEN RAISE EXCEPTION 'request hash mismatch'; END IF;
  SELECT * INTO existing FROM orders WHERE agent_id=p_agent_id AND idempotency_key=p_idempotency_key FOR KEY SHARE;
  IF FOUND THEN
    IF existing.request_hash = p_request_hash THEN RETURN existing.id; END IF;
    RAISE EXCEPTION 'same idempotency key has conflicting canonical hash';
  END IF;
  SELECT mo.price INTO authoritative_price FROM market_observations mo
  JOIN trading_sessions ts ON ts.session_id=mo.session_id
  WHERE mo.instrument_id=p_instrument_id AND mo.verified AND NOT mo.warning AND NOT mo.suspended
    AND mo.traded_at<=p_decision_at AND mo.retrieved_at<=p_decision_at
    AND mo.traded_at BETWEEN ts.opens_at AND ts.closes_at
    AND mo.retrieved_at-mo.traded_at BETWEEN interval '15 minutes' AND interval '20 minutes'
  ORDER BY mo.traded_at DESC,mo.id DESC LIMIT 1;
  IF authoritative_price IS NULL THEN RAISE EXCEPTION 'latest verified pre-decision observation is required'; END IF;
  INSERT INTO orders(id,agent_id,decision_id,idempotency_key,side,instrument_id,quantity,
                     decision_at,observed_price,request_json,request_hash)
    VALUES(p_id,p_agent_id,p_decision_id,p_idempotency_key,p_side,p_instrument_id,p_quantity,
           p_decision_at,authoritative_price,p_request_json,p_request_hash);
  RETURN p_id;
END $$;

CREATE OR REPLACE FUNCTION cancel_order(
 p_outcome_id uuid,p_order_id uuid,p_agent_id uuid,p_idempotency_key text,
 p_outcome_json jsonb,p_outcome_hash sha256_hex,p_occurred_at timestamptz
) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE existing order_outcomes%ROWTYPE;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('lifecycle',0));
  PERFORM pg_advisory_xact_lock(hashtextextended(p_agent_id::text,0));
  IF (SELECT status FROM contest_state WHERE singleton FOR SHARE) <> 'RUNNING' THEN
    RAISE EXCEPTION 'contest is not running';
  END IF;
  PERFORM 1 FROM orders WHERE id=p_order_id AND agent_id=p_agent_id FOR KEY SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'order ownership mismatch'; END IF;
  SELECT * INTO existing FROM order_outcomes WHERE order_id=p_order_id FOR KEY SHARE;
  IF FOUND THEN
    IF existing.idempotency_key=p_idempotency_key AND existing.outcome_hash=p_outcome_hash THEN RETURN existing.id; END IF;
    RAISE EXCEPTION 'order already has terminal outcome';
  END IF;
  INSERT INTO order_outcomes VALUES
    (p_outcome_id,p_order_id,p_agent_id,'CANCELLED',p_idempotency_key,p_outcome_json,p_outcome_hash,p_occurred_at);
  RETURN p_outcome_id;
END $$;

CREATE OR REPLACE FUNCTION record_fill(
 p_fill_id uuid,p_ledger_id uuid,p_outcome_id uuid,p_order_id uuid,p_agent_id uuid,
 p_observation_id uuid,p_quantity integer,p_execution_price numeric,p_gross numeric,p_fee numeric,
 p_slippage numeric,p_executed_at timestamptz,p_fill_json jsonb,p_fill_hash sha256_hex,
 p_ledger_json jsonb,p_ledger_hash sha256_hex,p_outcome_json jsonb,p_outcome_hash sha256_hex,
 p_idempotency_key text
) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE
  o orders%ROWTYPE;
  obs market_observations%ROWTYPE;
  s trading_sessions%ROWTYPE;
  existing order_outcomes%ROWTYPE;
  delta numeric;
  qdelta integer;
  etype text;
  derived_adv numeric;
  history_count integer;
  marked_capital numeric;
  issuer_exposure numeric;
  expected_slippage numeric;
  expected_execution numeric;
  expected_gross numeric;
  expected_fee numeric;
  slippage_adv numeric;
  active_tier fee_tier;
  trade_count integer;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('lifecycle',0));
  PERFORM pg_advisory_xact_lock(hashtextextended(p_agent_id::text,0));
  SELECT * INTO STRICT o FROM orders WHERE id=p_order_id AND agent_id=p_agent_id FOR KEY SHARE;
  SELECT * INTO existing FROM order_outcomes WHERE order_id=p_order_id FOR KEY SHARE;
  IF FOUND THEN
    IF existing.idempotency_key=p_idempotency_key AND existing.outcome_hash=p_outcome_hash AND existing.status='FILLED' THEN
      RETURN (SELECT id FROM fills WHERE order_id=p_order_id);
    END IF;
    RAISE EXCEPTION 'order already has terminal outcome';
  END IF;
  IF (SELECT status FROM contest_state WHERE singleton FOR SHARE) <> 'RUNNING' THEN
    RAISE EXCEPTION 'contest is not running';
  END IF;
  SELECT * INTO STRICT obs FROM market_observations WHERE id=p_observation_id FOR KEY SHARE;
  SELECT * INTO STRICT s FROM trading_sessions WHERE session_id=obs.session_id FOR KEY SHARE;
  IF obs.instrument_id<>o.instrument_id OR NOT obs.verified OR obs.warning OR obs.suspended
     OR obs.traded_at<o.decision_at OR obs.traded_at<s.opens_at OR obs.traded_at>s.closes_at
     OR obs.retrieved_at-obs.traded_at<interval '15 minutes'
     OR obs.retrieved_at-obs.traded_at>interval '20 minutes'
     OR p_executed_at<>obs.retrieved_at THEN
    RAISE EXCEPTION 'observation is not eligible for order';
  END IF;
  IF EXISTS (SELECT 1 FROM contest_state_events pause_event
    WHERE pause_event.to_status='PAUSED' AND obs.traded_at>=pause_event.occurred_at
      AND obs.traded_at<COALESCE((SELECT min(resume_event.occurred_at) FROM contest_state_events resume_event
        WHERE resume_event.from_status='PAUSED' AND resume_event.to_status='RUNNING'
          AND resume_event.occurred_at>pause_event.occurred_at),'infinity'::timestamptz)) THEN
    RAISE EXCEPTION 'observations traded during a pause are ineligible';
  END IF;

  SELECT count(*),avg(stats.traded_value) INTO history_count,derived_adv
  FROM (SELECT session_id FROM trading_sessions WHERE session_day<s.session_day
        ORDER BY session_day DESC LIMIT 20) required
  JOIN instrument_session_stats stats ON stats.session_id=required.session_id
       AND stats.instrument_id=o.instrument_id AND stats.complete;
  IF o.side='BUY' AND (history_count<>20 OR obs.complete_history_sessions<20
      OR obs.average_daily_value_20<>round(derived_adv,2)
      OR obs.price*o.quantity>derived_adv*0.01) THEN
    RAISE EXCEPTION 'twenty consecutive sessions and one percent ADV are required';
  END IF;

  IF EXISTS (
    SELECT 1 FROM market_observations earlier
    JOIN trading_sessions es ON es.session_id=earlier.session_id
    WHERE earlier.instrument_id=o.instrument_id AND earlier.verified AND NOT earlier.warning AND NOT earlier.suspended
      AND earlier.traded_at>=o.decision_at AND earlier.traded_at>=es.opens_at AND earlier.traded_at<=es.closes_at
      AND earlier.retrieved_at-earlier.traded_at BETWEEN interval '15 minutes' AND interval '20 minutes'
      AND NOT EXISTS (SELECT 1 FROM contest_state_events pause_event
        WHERE pause_event.to_status='PAUSED' AND earlier.traded_at>=pause_event.occurred_at
          AND earlier.traded_at<COALESCE((SELECT min(resume_event.occurred_at) FROM contest_state_events resume_event
            WHERE resume_event.from_status='PAUSED' AND resume_event.to_status='RUNNING'
              AND resume_event.occurred_at>pause_event.occurred_at),'infinity'::timestamptz))
      AND (earlier.traded_at,earlier.id)<(obs.traded_at,obs.id)
      AND (o.side='SELL' OR (earlier.complete_history_sessions>=20
           AND earlier.average_daily_value_20=round(derived_adv,2)
           AND earlier.price*o.quantity<=derived_adv*0.01))
  ) THEN RAISE EXCEPTION 'observation is not the first eligible quote'; END IF;

  SELECT b.cash + COALESCE(sum(p.quantity*mark.price),0),b.fee_tier,b.stock_trade_count
    INTO marked_capital,active_tier,trade_count
  FROM account_balances b
  LEFT JOIN positions p ON p.agent_id=b.agent_id AND p.quantity>0
  LEFT JOIN LATERAL (
    SELECT mo.price FROM market_observations mo
    JOIN trading_sessions ms ON ms.session_id=mo.session_id
    WHERE mo.instrument_id=p.instrument_id AND mo.verified AND NOT mo.warning AND NOT mo.suspended
      AND mo.session_id=obs.session_id AND mo.traded_at BETWEEN ms.opens_at AND ms.closes_at
      AND mo.traded_at<=obs.traded_at AND obs.traded_at-mo.traded_at<=interval '20 minutes'
      AND mo.retrieved_at-mo.traded_at BETWEEN interval '15 minutes' AND interval '20 minutes'
      AND mo.retrieved_at<=p_executed_at
    ORDER BY mo.traded_at DESC,mo.id DESC LIMIT 1
  ) mark ON true
  WHERE b.agent_id=p_agent_id
  GROUP BY b.cash,b.fee_tier,b.stock_trade_count;
  IF marked_capital IS NULL OR EXISTS (
      SELECT 1 FROM positions p WHERE p.agent_id=p_agent_id AND p.quantity>0 AND NOT EXISTS (
        SELECT 1 FROM market_observations mo JOIN trading_sessions ms ON ms.session_id=mo.session_id
        WHERE mo.instrument_id=p.instrument_id AND mo.verified AND NOT mo.warning AND NOT mo.suspended
          AND mo.session_id=obs.session_id AND mo.traded_at BETWEEN ms.opens_at AND ms.closes_at
          AND mo.traded_at<=obs.traded_at AND obs.traded_at-mo.traded_at<=interval '20 minutes'
          AND mo.retrieved_at-mo.traded_at BETWEEN interval '15 minutes' AND interval '20 minutes'
          AND mo.retrieved_at<=p_executed_at)) THEN
    RAISE EXCEPTION 'verified current marks are required';
  END IF;
  IF active_tier='MINI' OR marked_capital>=50000 OR trade_count>=500 THEN active_tier:='MINI'; END IF;

  slippage_adv:=CASE WHEN o.side='BUY' THEN derived_adv ELSE obs.average_daily_value_20 END;
  expected_slippage:=round(least(0.01,greatest(0.001,
    CASE WHEN obs.bid IS NOT NULL AND obs.ask IS NOT NULL THEN (obs.ask-obs.bid)/(2*obs.price) ELSE 0.001 END)
    +0.0025*sqrt((obs.price*o.quantity/slippage_adv)::numeric)),8);
  expected_execution:=round(obs.price*(CASE WHEN o.side='BUY' THEN 1+expected_slippage ELSE 1-expected_slippage END),4);
  expected_gross:=round(expected_execution*o.quantity,2);
  expected_fee:=CASE WHEN active_tier='STARTER' THEN 0 ELSE greatest(1,round(expected_gross*0.0025,2)) END;
  IF o.quantity<>p_quantity OR p_slippage<>expected_slippage OR p_execution_price<>expected_execution
     OR p_gross<>expected_gross OR p_fee<>expected_fee THEN
    RAISE EXCEPTION 'fill price, slippage, commission, or arithmetic differs from authority';
  END IF;

  IF o.side='BUY' THEN
    SELECT COALESCE(sum(p.quantity*mark.price),0)+obs.price*o.quantity INTO issuer_exposure
    FROM instruments target
    LEFT JOIN positions p ON p.agent_id=p_agent_id
    LEFT JOIN instruments held ON held.id=p.instrument_id AND held.issuer_id=target.issuer_id
    LEFT JOIN LATERAL (SELECT mo.price FROM market_observations mo WHERE mo.instrument_id=p.instrument_id
      AND mo.verified AND NOT mo.warning AND NOT mo.suspended AND mo.session_id=obs.session_id
      AND mo.traded_at<=obs.traded_at AND obs.traded_at-mo.traded_at<=interval '20 minutes'
      AND mo.retrieved_at-mo.traded_at BETWEEN interval '15 minutes' AND interval '20 minutes'
      AND mo.retrieved_at<=p_executed_at ORDER BY mo.traded_at DESC,mo.id DESC LIMIT 1) mark ON held.id IS NOT NULL
    WHERE target.id=o.instrument_id;
    IF issuer_exposure/(marked_capital+obs.price*o.quantity-expected_gross-expected_fee)>0.25 THEN
      RAISE EXCEPTION 'issuer concentration exceeds twenty-five percent';
    END IF;
    delta:=-(expected_gross+expected_fee); qdelta:=o.quantity; etype:='BUY_FILL';
  ELSE delta:=expected_gross-expected_fee; qdelta:=-o.quantity; etype:='SELL_FILL'; END IF;
  INSERT INTO ledger_events(id,agent_id,event_type,instrument_id,order_id,cash_delta,quantity_delta,
    unit_price,gross,fee,occurred_at,event_json,event_hash)
    VALUES(p_ledger_id,p_agent_id,etype,o.instrument_id,p_order_id,delta,qdelta,
      expected_execution,expected_gross,expected_fee,p_executed_at,p_ledger_json,p_ledger_hash);
  INSERT INTO fills VALUES(p_fill_id,p_order_id,p_agent_id,p_ledger_id,p_observation_id,o.quantity,
    expected_execution,expected_gross,expected_fee,expected_slippage,p_executed_at,p_fill_json,p_fill_hash);
  INSERT INTO order_outcomes VALUES(p_outcome_id,p_order_id,p_agent_id,'FILLED',p_idempotency_key,
    p_outcome_json,p_outcome_hash,p_executed_at);
  UPDATE account_balances SET stock_trade_count=stock_trade_count+1,fee_tier=active_tier
    WHERE agent_id=p_agent_id;
  RETURN p_fill_id;
END $$;

CREATE OR REPLACE FUNCTION apply_corporate_action(
 p_action_id uuid,p_agent_id uuid,p_application_id uuid,p_ledger_id uuid,p_applied_at timestamptz
) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE
  action corporate_actions%ROWTYPE;
  prior corporate_action_applications%ROWTYPE;
  held positions%ROWTYPE;
  target positions%ROWTYPE;
  event_value jsonb;
  cash_change numeric(20,2):=0;
  quantity_change integer:=0;
  average_after numeric(20,4);
  entitled integer;
  numerator integer;
  denominator integer;
  exact_quantity numeric;
  whole_quantity integer;
  fraction numeric;
  target_id uuid;
  second_ledger uuid;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('lifecycle',0));
  PERFORM pg_advisory_xact_lock(hashtextextended(p_agent_id::text,0));
  SELECT * INTO prior FROM corporate_action_applications
    WHERE corporate_action_id=p_action_id AND agent_id=p_agent_id FOR KEY SHARE;
  IF FOUND THEN RETURN prior.ledger_event_id; END IF;
  IF (SELECT status FROM contest_state WHERE singleton FOR SHARE)<>'RUNNING' THEN
    RAISE EXCEPTION 'contest is not running';
  END IF;
  SELECT * INTO STRICT action FROM corporate_actions WHERE id=p_action_id FOR KEY SHARE;
  SELECT * INTO held FROM positions WHERE agent_id=p_agent_id AND instrument_id=action.instrument_id;
  event_value:=jsonb_build_object('external_reference',action.external_reference,
    'action_type',action.action_type,'normalized',action.normalized_json,'agent_id',p_agent_id);

  IF action.action_type='DIVIDEND' THEN
    IF (action.normalized_json->>'per_share')::numeric<0
       OR (action.normalized_json->>'ownership_close')::timestamptz >= (action.normalized_json->>'ex_date')::date
       OR p_applied_at<(action.normalized_json->>'payment_at')::timestamptz THEN
      RAISE EXCEPTION 'invalid dividend normalization';
    END IF;
    SELECT COALESCE(sum(quantity_delta),0) INTO entitled FROM ledger_events
      WHERE agent_id=p_agent_id AND instrument_id=action.instrument_id
        AND occurred_at<=(action.normalized_json->>'ownership_close')::timestamptz;
    cash_change:=round(entitled*(action.normalized_json->>'per_share')::numeric,2);
  ELSIF action.action_type='SPLIT' THEN
    numerator:=(action.normalized_json->>'numerator')::integer;
    denominator:=(action.normalized_json->>'denominator')::integer;
    IF numerator<=0 OR denominator<=0 THEN RAISE EXCEPTION 'invalid split ratio'; END IF;
    IF FOUND THEN
      exact_quantity:=held.quantity*numerator::numeric/denominator;
      whole_quantity:=floor(exact_quantity); fraction:=exact_quantity-whole_quantity;
      quantity_change:=whole_quantity-held.quantity;
      average_after:=round(held.average_cost*denominator::numeric/numerator,4);
      IF fraction>0 THEN INSERT INTO fractional_entitlements VALUES(gen_random_uuid(),p_action_id,p_agent_id,
        action.instrument_id,fraction,average_after,p_applied_at); END IF;
    END IF;
  ELSIF action.action_type='CASH_MERGER' OR (action.action_type='DELISTING' AND action.normalized_json ? 'official_proceeds') THEN
    IF (action.normalized_json->>CASE WHEN action.action_type='CASH_MERGER' THEN 'per_share' ELSE 'official_proceeds' END)::numeric<0
      THEN RAISE EXCEPTION 'invalid official proceeds'; END IF;
    IF FOUND THEN
      cash_change:=round(held.quantity*(action.normalized_json->>CASE WHEN action.action_type='CASH_MERGER' THEN 'per_share' ELSE 'official_proceeds' END)::numeric,2);
      quantity_change:=-held.quantity; average_after:=0;
    END IF;
  ELSIF action.action_type='DELISTING' THEN
    IF FOUND THEN UPDATE positions SET frozen=true,updated_at=p_applied_at
      WHERE agent_id=p_agent_id AND instrument_id=action.instrument_id; END IF;
  ELSIF action.action_type='STOCK_MERGER' THEN
    target_id:=(action.normalized_json->>'target_instrument_id')::uuid;
    numerator:=(action.normalized_json->>'numerator')::integer;
    denominator:=(action.normalized_json->>'denominator')::integer;
    IF target_id=action.instrument_id OR numerator<=0 OR denominator<=0 THEN RAISE EXCEPTION 'invalid stock merger'; END IF;
    PERFORM 1 FROM instruments WHERE id=target_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'stock merger target is absent'; END IF;
    IF held.agent_id IS NOT NULL THEN
      exact_quantity:=held.quantity*numerator::numeric/denominator;
      whole_quantity:=floor(exact_quantity); fraction:=exact_quantity-whole_quantity;
      average_after:=round(held.average_cost*denominator::numeric/numerator,4);
      INSERT INTO ledger_events(id,agent_id,event_type,instrument_id,corporate_action_id,cash_delta,
        quantity_delta,average_cost_after,occurred_at,event_json,event_hash)
      VALUES(p_ledger_id,p_agent_id,'STOCK_MERGER',action.instrument_id,p_action_id,0,-held.quantity,0,
        p_applied_at,event_value,canonical_jsonb_sha256(event_value));
      second_ledger:=gen_random_uuid();
      SELECT * INTO target FROM positions WHERE agent_id=p_agent_id AND instrument_id=target_id;
      INSERT INTO ledger_events(id,agent_id,event_type,instrument_id,corporate_action_id,cash_delta,
        quantity_delta,average_cost_after,occurred_at,event_json,event_hash)
      VALUES(second_ledger,p_agent_id,'STOCK_MERGER',target_id,p_action_id,0,whole_quantity,
        CASE WHEN whole_quantity=0 THEN COALESCE(target.average_cost,0)
             ELSE round((COALESCE(target.average_cost*target.quantity,0)+average_after*whole_quantity)
                        /(COALESCE(target.quantity,0)+whole_quantity),4) END,
        p_applied_at,event_value,canonical_jsonb_sha256(event_value));
      IF fraction>0 THEN INSERT INTO fractional_entitlements VALUES(gen_random_uuid(),p_action_id,p_agent_id,
        target_id,fraction,average_after,p_applied_at); END IF;
    ELSE
      INSERT INTO ledger_events(id,agent_id,event_type,instrument_id,corporate_action_id,cash_delta,
        quantity_delta,occurred_at,event_json,event_hash)
      VALUES(p_ledger_id,p_agent_id,'STOCK_MERGER',action.instrument_id,p_action_id,0,0,p_applied_at,
        event_value,canonical_jsonb_sha256(event_value));
    END IF;
    INSERT INTO corporate_action_applications VALUES(p_application_id,p_action_id,p_agent_id,p_ledger_id,
      event_value,canonical_jsonb_sha256(event_value),p_applied_at);
    RETURN p_ledger_id;
  ELSIF action.action_type='CORRECTION' THEN
    cash_change:=COALESCE((action.normalized_json->>'cash_delta')::numeric,0);
    quantity_change:=COALESCE((action.normalized_json->>'quantity_delta')::integer,0);
    average_after:=NULLIF(action.normalized_json->>'average_cost_after','')::numeric;
  ELSE RAISE EXCEPTION 'unsupported corporate action'; END IF;

  INSERT INTO ledger_events(id,agent_id,event_type,instrument_id,corporate_action_id,cash_delta,
    quantity_delta,average_cost_after,occurred_at,event_json,event_hash)
  VALUES(p_ledger_id,p_agent_id,action.action_type::text,action.instrument_id,p_action_id,cash_change,
    quantity_change,average_after,p_applied_at,event_value,canonical_jsonb_sha256(event_value));
  INSERT INTO corporate_action_applications VALUES(p_application_id,p_action_id,p_agent_id,p_ledger_id,
    event_value,canonical_jsonb_sha256(event_value),p_applied_at);
  RETURN p_ledger_id;
END $$;

CREATE OR REPLACE FUNCTION claim_scheduled_run(p_now timestamptz,p_lease interval,p_token uuid)
RETURNS SETOF scheduled_agent_runs LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE claimed scheduled_agent_runs%ROWTYPE;
BEGIN
  IF NOT pg_try_advisory_xact_lock(hashtextextended('scheduled-runs',0)) THEN RETURN; END IF;
  SELECT * INTO claimed FROM scheduled_agent_runs
   WHERE ((status='PENDING' AND next_attempt_at<=p_now) OR (status='CLAIMED' AND lease_until<=p_now))
     AND deadline_at>=p_now
   ORDER BY scheduled_at FOR UPDATE SKIP LOCKED LIMIT 1;
  IF NOT FOUND THEN RETURN; END IF;
  UPDATE scheduled_agent_runs SET status='CLAIMED',claim_token=p_token,lease_until=p_now+p_lease,
    attempt_count=attempt_count+1 WHERE id=claimed.id RETURNING * INTO claimed;
  RETURN NEXT claimed;
END $$;

CREATE OR REPLACE FUNCTION complete_scheduled_run(
 p_id uuid,p_token uuid,p_status run_status,p_now timestamptz,p_error text,p_retry_at timestamptz
) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE r scheduled_agent_runs%ROWTYPE;
BEGIN
 SELECT * INTO STRICT r FROM scheduled_agent_runs WHERE id=p_id FOR UPDATE;
 IF r.status<>'CLAIMED' OR r.claim_token<>p_token THEN RAISE EXCEPTION 'run claim lost'; END IF;
 IF p_now>r.deadline_at AND p_status<>'MISSED' THEN
   RAISE EXCEPTION 'run completion exceeded immutable deadline';
 END IF;
 IF p_status='FAILED' AND p_retry_at IS NOT NULL AND p_retry_at<=r.deadline_at THEN
   UPDATE scheduled_agent_runs SET status='PENDING',claim_token=NULL,lease_until=NULL,
     next_attempt_at=p_retry_at,last_error=p_error WHERE id=p_id;
 ELSIF p_status IN ('SUCCEEDED','FAILED','MISSED') THEN
   UPDATE scheduled_agent_runs SET status=p_status,claim_token=NULL,lease_until=NULL,
     last_error=p_error,completed_at=p_now WHERE id=p_id;
 ELSE RAISE EXCEPTION 'invalid run completion'; END IF;
END $$;

CREATE OR REPLACE FUNCTION transition_contest(
 p_event_id uuid,p_from contest_status,p_to contest_status,p_reason text,p_actor text,
 p_key text,p_request jsonb,p_hash sha256_hex,p_at timestamptz
) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE existing contest_state_events%ROWTYPE;
BEGIN
 PERFORM pg_advisory_xact_lock(hashtextextended('lifecycle',0));
 SELECT * INTO existing FROM contest_state_events WHERE idempotency_key=p_key FOR KEY SHARE;
 IF FOUND THEN
   IF existing.request_hash=p_hash THEN RETURN existing.id; END IF;
   RAISE EXCEPTION 'same idempotency key has conflicting canonical hash';
 END IF;
 IF (SELECT status FROM contest_state WHERE singleton FOR UPDATE)<>p_from THEN RAISE EXCEPTION 'stale contest lifecycle'; END IF;
 IF p_to='FINISHED' THEN RAISE EXCEPTION 'use finalize_contest for atomic ranking'; END IF;
 UPDATE contest_state SET status=p_to,
   started_at=CASE WHEN p_to='RUNNING' AND started_at IS NULL THEN p_at ELSE started_at END
   WHERE singleton;
 INSERT INTO contest_state_events VALUES(p_event_id,p_key,p_request,p_hash,p_from,p_to,p_reason,p_actor,p_at);
 RETURN p_event_id;
END $$;

CREATE OR REPLACE FUNCTION finalize_contest(
 p_reference text,p_event_id uuid,p_key text,p_request jsonb,p_hash sha256_hex,p_at timestamptz
) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE
  prior contest_state_events%ROWTYPE;
  state_before contest_status;
  account_row account_balances%ROWTYPE;
  position_row positions%ROWTYPE;
  close_row market_observations%ROWTYPE;
  close_count integer;
  marked_capital numeric;
  active_tier fee_tier;
  slip numeric;
  execution numeric;
  gross_value numeric;
  commission numeric;
  proceeds numeric;
  liquidation_order uuid;
  liquidation_ledger uuid;
  liquidation_fill uuid;
  liquidation_outcome uuid;
  event_value jsonb;
BEGIN
 PERFORM pg_advisory_xact_lock(hashtextextended('lifecycle',0));
 SELECT * INTO prior FROM contest_state_events WHERE idempotency_key=p_key FOR KEY SHARE;
 IF FOUND THEN
   IF prior.request_hash=p_hash AND prior.to_status='FINISHED' THEN RETURN; END IF;
   RAISE EXCEPTION 'same idempotency key has conflicting canonical hash';
 END IF;
 IF p_hash<>canonical_jsonb_sha256(p_request) THEN RAISE EXCEPTION 'request hash mismatch'; END IF;
 SELECT status INTO state_before FROM contest_state WHERE singleton FOR UPDATE;
 IF state_before <> 'RUNNING' THEN RAISE EXCEPTION 'contest may only finish once from running state'; END IF;
 IF p_reference<>'XSTO-2026-12-30-final' OR p_key<>'XSTO-2026-12-30-final'
    OR p_at<>timestamptz '2026-12-30 16:50:00Z'
    OR p_request<>jsonb_build_object('session_id','XSTO-2026-12-30') THEN
   RAISE EXCEPTION 'finalization identity and time must match the pinned XSTO contract';
 END IF;
 IF (SELECT count(*) FROM trading_sessions WHERE is_final)<>1 THEN
   RAISE EXCEPTION 'exactly one official final session is required';
 END IF;

 -- Validate every required official PATS close before any liquidation mutation.
 FOR position_row IN SELECT * FROM positions WHERE quantity>0 AND NOT frozen LOOP
   SELECT count(*) INTO close_count
   FROM market_observations mo JOIN trading_sessions ts ON ts.session_id=mo.session_id
   WHERE mo.instrument_id=position_row.instrument_id AND ts.is_final AND mo.is_official_pats
     AND mo.verified AND NOT mo.warning AND NOT mo.suspended AND mo.traded_at=ts.closes_at
     AND mo.retrieved_at-mo.traded_at BETWEEN interval '15 minutes' AND interval '20 minutes'
     AND mo.retrieved_at<=p_at;
   IF close_count<>1 THEN RAISE EXCEPTION 'one unambiguous official PATS close is required'; END IF;
 END LOOP;

 FOR account_row IN SELECT * FROM account_balances ORDER BY agent_id LOOP
   PERFORM pg_advisory_xact_lock(hashtextextended(account_row.agent_id::text,0));
   SELECT account_row.cash+COALESCE(sum(p.quantity*mo.price),0) INTO marked_capital
   FROM positions p
   JOIN LATERAL (SELECT observation.price FROM market_observations observation
     JOIN trading_sessions ts ON ts.session_id=observation.session_id
     WHERE observation.instrument_id=p.instrument_id AND ts.is_final AND observation.is_official_pats
       AND observation.verified AND NOT observation.warning AND NOT observation.suspended
       AND observation.traded_at=ts.closes_at
       AND observation.retrieved_at-observation.traded_at BETWEEN interval '15 minutes' AND interval '20 minutes'
       AND observation.retrieved_at<=p_at
     ORDER BY observation.id LIMIT 1) mo ON NOT p.frozen
   WHERE p.agent_id=account_row.agent_id;
   active_tier:=account_row.fee_tier;
   IF active_tier='MINI' OR marked_capital>=50000 OR account_row.stock_trade_count>=500 THEN active_tier:='MINI'; END IF;

   FOR position_row IN SELECT * FROM positions WHERE agent_id=account_row.agent_id AND quantity>0 AND NOT frozen
                       ORDER BY instrument_id LOOP
     SELECT mo.* INTO STRICT close_row FROM market_observations mo
       JOIN trading_sessions ts ON ts.session_id=mo.session_id
       WHERE mo.instrument_id=position_row.instrument_id AND ts.is_final AND mo.is_official_pats
         AND mo.verified AND NOT mo.warning AND NOT mo.suspended AND mo.traded_at=ts.closes_at
         AND mo.retrieved_at-mo.traded_at BETWEEN interval '15 minutes' AND interval '20 minutes'
         AND mo.retrieved_at<=p_at;
     IF active_tier='STARTER' AND account_row.stock_trade_count>=500 THEN active_tier:='MINI'; END IF;
     slip:=round(least(0.01,greatest(0.001,
       CASE WHEN close_row.bid IS NOT NULL AND close_row.ask IS NOT NULL
            THEN (close_row.ask-close_row.bid)/(2*close_row.price) ELSE 0.001 END)
       +0.0025*sqrt((close_row.price*position_row.quantity/close_row.average_daily_value_20)::numeric)),8);
     execution:=round(close_row.price*(1-slip),4);
     gross_value:=round(execution*position_row.quantity,2);
     commission:=CASE WHEN active_tier='STARTER' THEN 0 ELSE greatest(1,round(gross_value*0.0025,2)) END;
     proceeds:=gross_value-commission;
     liquidation_order:=gen_random_uuid(); liquidation_ledger:=gen_random_uuid();
     liquidation_fill:=gen_random_uuid(); liquidation_outcome:=gen_random_uuid();
     event_value:=jsonb_build_object('kind','FINAL_LIQUIDATION','reference',p_reference,
       'instrument_id',position_row.instrument_id,'quantity',position_row.quantity,
       'close_observation_id',close_row.id,'execution_price',execution,'gross',gross_value,'fee',commission);
     INSERT INTO orders(id,agent_id,decision_id,idempotency_key,side,instrument_id,quantity,
       decision_at,observed_price,request_json,request_hash)
     VALUES(liquidation_order,account_row.agent_id,p_reference||':'||position_row.instrument_id,
       p_reference||':'||position_row.instrument_id,'SELL',position_row.instrument_id,position_row.quantity,
       close_row.traded_at,close_row.price,event_value,canonical_jsonb_sha256(event_value));
     INSERT INTO ledger_events(id,agent_id,event_type,instrument_id,order_id,cash_delta,quantity_delta,
       unit_price,gross,fee,occurred_at,event_json,event_hash)
     VALUES(liquidation_ledger,account_row.agent_id,'FINAL_LIQUIDATION',position_row.instrument_id,
       liquidation_order,proceeds,-position_row.quantity,execution,gross_value,commission,
       close_row.retrieved_at,event_value,canonical_jsonb_sha256(event_value));
     INSERT INTO fills VALUES(liquidation_fill,liquidation_order,account_row.agent_id,liquidation_ledger,
       close_row.id,position_row.quantity,execution,gross_value,commission,slip,close_row.retrieved_at,
       event_value,canonical_jsonb_sha256(event_value));
     INSERT INTO order_outcomes VALUES(liquidation_outcome,liquidation_order,account_row.agent_id,'FILLED',
       p_reference||':'||position_row.instrument_id,event_value,canonical_jsonb_sha256(event_value),close_row.retrieved_at);
     account_row.stock_trade_count:=account_row.stock_trade_count+1;
     UPDATE account_balances SET stock_trade_count=account_row.stock_trade_count,fee_tier=active_tier
       WHERE agent_id=account_row.agent_id;
   END LOOP;
 END LOOP;

 INSERT INTO final_rankings(id,reference,agent_id,rank,net_liquidation_value,input_json,input_hash,finalized_at)
 SELECT gen_random_uuid(),p_reference,b.agent_id,rank() OVER (ORDER BY b.cash DESC),b.cash,
        jsonb_build_object('agent_id',b.agent_id,'authoritative_cash',b.cash,'reference',p_reference),
        canonical_jsonb_sha256(jsonb_build_object('agent_id',b.agent_id,'authoritative_cash',b.cash,'reference',p_reference)),p_at
 FROM account_balances b;
 INSERT INTO contest_state_events VALUES(p_event_id,p_key,p_request,p_hash,state_before,'FINISHED',
   'authoritative official-PATS final liquidation','system',p_at);
 UPDATE contest_state SET status='FINISHED',finished_at=p_at WHERE singleton;
END $$;

INSERT INTO ledger_events(id,agent_id,event_type,cash_delta,quantity_delta,occurred_at,event_json,event_hash)
SELECT gen_random_uuid(),id,'INITIAL_FUNDING',30000.00,0,created_at,
       jsonb_build_object('kind','INITIAL_FUNDING','amount','30000.00','currency','SEK','agent_id',id),
       canonical_jsonb_sha256(jsonb_build_object('kind','INITIAL_FUNDING','amount','30000.00','currency','SEK','agent_id',id))
FROM agents;

REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA public FROM ai_stocks_runtime;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM ai_stocks_runtime;
GRANT USAGE ON SCHEMA public TO ai_stocks_runtime;
GRANT SELECT ON agents,contest_state,instruments,trading_sessions,instrument_session_stats,raw_market_reports,market_observations,prompts,scheduled_agent_runs,agent_runs,
 strategies,orders,order_outcomes,ledger_events,fills,corporate_actions,corporate_action_applications,
 account_balances,positions,portfolio_snapshots,final_rankings,contest_state_events,fractional_entitlements TO ai_stocks_runtime;
GRANT INSERT ON instruments,trading_sessions,instrument_session_stats,raw_market_reports,market_observations,prompts,scheduled_agent_runs,agent_runs,strategies,
 portfolio_snapshots TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION submit_order(uuid,uuid,text,text,order_side,uuid,integer,timestamptz,jsonb,sha256_hex) TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION cancel_order(uuid,uuid,uuid,text,jsonb,sha256_hex,timestamptz) TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION record_fill(uuid,uuid,uuid,uuid,uuid,uuid,integer,numeric,numeric,numeric,numeric,timestamptz,jsonb,sha256_hex,jsonb,sha256_hex,jsonb,sha256_hex,text) TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION apply_corporate_action(uuid,uuid,uuid,uuid,timestamptz) TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION claim_scheduled_run(timestamptz,interval,uuid) TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION complete_scheduled_run(uuid,uuid,run_status,timestamptz,text,timestamptz) TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION transition_contest(uuid,contest_status,contest_status,text,text,text,jsonb,sha256_hex,timestamptz) TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION finalize_contest(text,uuid,text,jsonb,sha256_hex,timestamptz) TO ai_stocks_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE ai_stocks_migrator IN SCHEMA public REVOKE ALL ON TABLES FROM ai_stocks_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE ai_stocks_migrator IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM ai_stocks_runtime;
RESET ROLE;
