-- Global races v2 migration ordinal 019. Separate from the legacy Swedish contest tables.
-- Seed instruments are explicitly non-production reference-index fixtures; no quote or FX is seeded.
CREATE TABLE v2_races (
  id uuid PRIMARY KEY,
  name text NOT NULL CHECK (length(name) BETWEEN 1 AND 120),
  kind text NOT NULL CHECK (kind IN ('human_sandbox','ai_league','mixed_exhibition')),
  status text NOT NULL CHECK (status IN ('draft','open','paused','finished')),
  initial_cash_dkk numeric(18,2) NOT NULL CHECK (initial_cash_dkk = 100000.00),
  created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
  UNIQUE (kind)
);

CREATE TABLE v2_participants (
  id uuid PRIMARY KEY,
  race_id uuid NOT NULL REFERENCES v2_races(id),
  principal text NOT NULL CHECK (principal = lower(btrim(principal)) AND length(principal) BETWEEN 3 AND 254),
  participant_type text NOT NULL CHECK (participant_type IN ('human','ai')),
  trusted_model_id text,
  display_name text NOT NULL CHECK (length(display_name) BETWEEN 1 AND 254),
  joined_at timestamptz NOT NULL,
  join_idempotency_key text NOT NULL CHECK (length(join_idempotency_key) BETWEEN 8 AND 128),
  join_request_hash text NOT NULL CHECK (join_request_hash ~ '^[0-9a-f]{64}$'),
  UNIQUE (race_id, principal),
  UNIQUE (race_id, principal, join_idempotency_key),
  UNIQUE (race_id, id),
  UNIQUE (id, participant_type),
  CHECK ((participant_type='human' AND trusted_model_id IS NULL)
      OR (participant_type='ai' AND length(trusted_model_id) BETWEEN 1 AND 100))
);

CREATE TABLE v2_ledger_events (
  id uuid PRIMARY KEY,
  participant_id uuid NOT NULL REFERENCES v2_participants(id),
  event_type text NOT NULL CHECK (event_type IN ('initial_cash','fill','cash_adjustment')),
  cash_delta_dkk numeric(18,2) NOT NULL,
  instrument_id text,
  quantity_delta bigint NOT NULL DEFAULT 0,
  order_id uuid,
  reference text NOT NULL CHECK (length(reference) BETWEEN 1 AND 200),
  metadata_json jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(metadata_json) = 'object'),
  occurred_at timestamptz NOT NULL,
  UNIQUE (participant_id, reference),
  CHECK (event_type <> 'initial_cash' OR
    (cash_delta_dkk = 100000.00 AND quantity_delta = 0 AND instrument_id IS NULL AND order_id IS NULL))
);
CREATE UNIQUE INDEX v2_one_initial_cash_per_participant
  ON v2_ledger_events(participant_id) WHERE event_type = 'initial_cash';

CREATE FUNCTION v2_ensure_participant(
  p_id uuid,p_race_id uuid,p_principal text,p_participant_type text,p_display_name text,
  p_trusted_model_id text,p_idempotency_key text,p_request_hash text,p_joined_at timestamptz)
RETURNS TABLE(id uuid,race_id uuid,principal text,participant_type text,display_name text,joined_at timestamptz,replayed boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE existing public.v2_participants%ROWTYPE;
DECLARE race_kind text;
BEGIN
  PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_race_id::text || ':' || p_principal,0));
  SELECT kind INTO race_kind FROM public.v2_races WHERE v2_races.id=p_race_id AND status='open';
  IF race_kind IS NULL THEN RAISE EXCEPTION 'race is absent or not open'; END IF;
  IF (p_participant_type='human' AND race_kind='ai_league') OR
     (p_participant_type='ai' AND race_kind='human_sandbox') THEN
    RAISE EXCEPTION 'participant type is not allowed in race';
  END IF;
  SELECT * INTO existing FROM public.v2_participants p
    WHERE p.race_id=p_race_id AND p.principal=p_principal;
  IF FOUND THEN
    IF existing.join_idempotency_key<>p_idempotency_key OR existing.join_request_hash<>p_request_hash OR
       existing.participant_type<>p_participant_type OR existing.trusted_model_id IS DISTINCT FROM p_trusted_model_id THEN
      RAISE EXCEPTION 'principal already joined with a different key or identity';
    END IF;
    RETURN QUERY SELECT existing.id,existing.race_id,existing.principal,existing.participant_type,
      existing.display_name,existing.joined_at,true;
    RETURN;
  END IF;
  INSERT INTO public.v2_participants(id,race_id,principal,participant_type,trusted_model_id,display_name,joined_at,
    join_idempotency_key,join_request_hash)
  VALUES(p_id,p_race_id,p_principal,p_participant_type,p_trusted_model_id,p_display_name,p_joined_at,
    p_idempotency_key,p_request_hash);
  INSERT INTO public.v2_ledger_events(id,participant_id,event_type,cash_delta_dkk,reference,occurred_at)
  VALUES(gen_random_uuid(),p_id,'initial_cash',100000.00,'initial:' || p_id::text,p_joined_at);
  RETURN QUERY SELECT p_id,p_race_id,p_principal,p_participant_type,p_display_name,p_joined_at,false;
END $$;
REVOKE ALL ON FUNCTION v2_ensure_participant(uuid,uuid,text,text,text,text,text,text,timestamptz) FROM PUBLIC;

CREATE TABLE v2_instruments (
  id text PRIMARY KEY CHECK (length(id) BETWEEN 1 AND 128),
  symbol text NOT NULL CHECK (length(symbol) BETWEEN 1 AND 64),
  name text NOT NULL CHECK (length(name) BETWEEN 1 AND 512),
  exchange text NOT NULL CHECK (length(exchange) BETWEEN 2 AND 32),
  country text NOT NULL CHECK (length(country) BETWEEN 2 AND 64),
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  provider text NOT NULL,
  provider_approved boolean NOT NULL DEFAULT false,
  fixture_only boolean NOT NULL DEFAULT true,
  indexed_at timestamptz NOT NULL
);
CREATE INDEX v2_instruments_symbol_search ON v2_instruments(lower(symbol));
CREATE INDEX v2_instruments_name_search ON v2_instruments(lower(name));

CREATE TABLE v2_verified_quotes (
  id uuid PRIMARY KEY,
  instrument_id text NOT NULL REFERENCES v2_instruments(id),
  price numeric(24,8) NOT NULL CHECK (price > 0),
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  observed_at timestamptz NOT NULL,
  available_at timestamptz NOT NULL CHECK (available_at >= observed_at),
  provider text NOT NULL,
  provider_approved boolean NOT NULL CHECK (provider_approved),
  source_sha256 text NOT NULL CHECK (source_sha256 ~ '^[0-9a-f]{64}$'),
  UNIQUE (instrument_id, observed_at, provider)
);

CREATE TABLE v2_verified_fx_rates (
  id uuid PRIMARY KEY,
  base_currency char(3) NOT NULL,
  quote_currency char(3) NOT NULL CHECK (quote_currency = 'DKK'),
  rate numeric(24,10) NOT NULL CHECK (rate > 0),
  observed_at timestamptz NOT NULL,
  provider text NOT NULL,
  provider_approved boolean NOT NULL CHECK (provider_approved),
  source_sha256 text NOT NULL CHECK (source_sha256 ~ '^[0-9a-f]{64}$'),
  UNIQUE (base_currency, quote_currency, observed_at, provider)
);

CREATE TABLE v2_orders (
  id uuid PRIMARY KEY,
  race_id uuid NOT NULL REFERENCES v2_races(id),
  participant_id uuid NOT NULL REFERENCES v2_participants(id),
  actor_type text NOT NULL CHECK (actor_type IN ('human','ai')),
  trusted_model_id text,
  instrument_id text NOT NULL REFERENCES v2_instruments(id),
  side text NOT NULL CHECK (side IN ('buy','sell')),
  quantity bigint NOT NULL CHECK (quantity BETWEEN 1 AND 100000),
  order_type text NOT NULL CHECK (order_type = 'market'),
  status text NOT NULL CHECK (status IN ('queued','cancelled')),
  note text CHECK (note IS NULL OR length(note) <= 500),
  rationale_json jsonb,
  evidence_json jsonb,
  idempotency_key text NOT NULL CHECK (length(idempotency_key) BETWEEN 8 AND 128),
  request_hash text NOT NULL CHECK (request_hash ~ '^[0-9a-f]{64}$'),
  submitted_at timestamptz NOT NULL,
  CHECK ((actor_type = 'human' AND participant_id IS NOT NULL AND trusted_model_id IS NULL AND rationale_json IS NULL AND evidence_json IS NULL)
      OR (actor_type = 'ai' AND participant_id IS NOT NULL AND length(trusted_model_id) BETWEEN 1 AND 100
          AND jsonb_typeof(rationale_json) = 'object' AND length(btrim(rationale_json->>'thesis')) BETWEEN 1 AND 2000
          AND jsonb_typeof(evidence_json) = 'array' AND jsonb_array_length(evidence_json) BETWEEN 1 AND 20)),
  UNIQUE (race_id, participant_id, idempotency_key)
);
ALTER TABLE v2_orders ADD CONSTRAINT v2_order_race_participant_fk
  FOREIGN KEY (race_id,participant_id) REFERENCES v2_participants(race_id,id);
ALTER TABLE v2_orders ADD CONSTRAINT v2_order_actor_participant_fk
  FOREIGN KEY (participant_id,actor_type) REFERENCES v2_participants(id,participant_type);
ALTER TABLE v2_ledger_events ADD CONSTRAINT v2_ledger_order_fk FOREIGN KEY (order_id) REFERENCES v2_orders(id);

CREATE TABLE v2_order_lifecycle_events (
  id uuid PRIMARY KEY,
  order_id uuid NOT NULL REFERENCES v2_orders(id),
  participant_id uuid NOT NULL REFERENCES v2_participants(id),
  event_type text NOT NULL CHECK (event_type = 'cancelled'),
  idempotency_key text NOT NULL CHECK (length(idempotency_key) BETWEEN 8 AND 128),
  request_hash text NOT NULL CHECK (request_hash ~ '^[0-9a-f]{64}$'),
  occurred_at timestamptz NOT NULL,
  UNIQUE (participant_id, idempotency_key),
  UNIQUE (order_id, event_type)
);

CREATE TRIGGER v2_participants_append_only BEFORE UPDATE OR DELETE OR TRUNCATE ON v2_participants
  FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER v2_ledger_events_append_only BEFORE UPDATE OR DELETE OR TRUNCATE ON v2_ledger_events
  FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER v2_orders_append_only BEFORE UPDATE OR DELETE OR TRUNCATE ON v2_orders
  FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER v2_order_lifecycle_append_only BEFORE UPDATE OR DELETE OR TRUNCATE ON v2_order_lifecycle_events
  FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();

INSERT INTO v2_races(id,name,kind,status,initial_cash_dkk) VALUES
 ('10000000-0000-0000-0000-000000000001','Human Sandbox','human_sandbox','open',100000.00),
 ('10000000-0000-0000-0000-000000000002','AI League','ai_league','open',100000.00),
 ('10000000-0000-0000-0000-000000000003','Mixed Exhibition','mixed_exhibition','open',100000.00);
INSERT INTO v2_instruments(id,symbol,name,exchange,country,currency,provider,provider_approved,fixture_only,indexed_at) VALUES
 ('novo-dk','NOVO B','Novo Nordisk A/S','XCSE','Denmark','DKK','approved-provider-contract-pending',false,true,'2026-08-20T00:00:00Z'),
 ('aapl-us','AAPL','Apple Inc.','XNAS','United States','USD','approved-provider-contract-pending',false,true,'2026-08-20T00:00:00Z'),
 ('asml-nl','ASML','ASML Holding N.V.','XAMS','Netherlands','EUR','approved-provider-contract-pending',false,true,'2026-08-20T00:00:00Z'),
 ('sony-jp','SONY','Sony Group Corp.','XTKS','Japan','JPY','approved-provider-contract-pending',false,true,'2026-08-20T00:00:00Z');

REVOKE ALL ON v2_races,v2_participants,v2_ledger_events,v2_instruments,v2_verified_quotes,
  v2_verified_fx_rates,v2_orders,v2_order_lifecycle_events FROM PUBLIC;
GRANT SELECT ON v2_races,v2_instruments,v2_verified_quotes,v2_verified_fx_rates TO ai_stocks_web_runtime;
GRANT SELECT ON v2_participants,v2_ledger_events,v2_orders,v2_order_lifecycle_events TO ai_stocks_web_runtime;
GRANT INSERT ON v2_orders,v2_order_lifecycle_events TO ai_stocks_web_runtime;
GRANT EXECUTE ON FUNCTION v2_ensure_participant(uuid,uuid,text,text,text,text,text,text,timestamptz)
  TO ai_stocks_web_runtime;
GRANT SELECT ON v2_races,v2_participants,v2_instruments,v2_verified_quotes,v2_verified_fx_rates,
  v2_orders,v2_ledger_events,v2_order_lifecycle_events TO ai_stocks_worker_runtime;
GRANT INSERT ON v2_verified_quotes,v2_verified_fx_rates,v2_ledger_events TO ai_stocks_worker_runtime;
