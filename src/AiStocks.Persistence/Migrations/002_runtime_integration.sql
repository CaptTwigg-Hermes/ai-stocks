-- Runtime integration state omitted from the initial domain schema.
SET LOCAL ROLE ai_stocks_migrator;

CREATE TYPE delivery_status AS ENUM ('RESERVED','SUCCEEDED','FAILED');

CREATE TABLE delivery_reservations (
    delivery_key text PRIMARY KEY CHECK (length(delivery_key) BETWEEN 1 AND 256),
    content_hash sha256_hex NOT NULL,
    status delivery_status NOT NULL,
    receipt text,
    last_error text,
    updated_at timestamptz NOT NULL,
    CHECK ((status = 'SUCCEEDED') = (receipt IS NOT NULL)),
    CHECK (status <> 'FAILED' OR last_error IS NOT NULL)
);

CREATE TABLE delivery_audits (
    id uuid PRIMARY KEY,
    delivery_key text NOT NULL REFERENCES delivery_reservations(delivery_key) ON DELETE RESTRICT,
    content_hash sha256_hex NOT NULL,
    status delivery_status NOT NULL CHECK (status IN ('SUCCEEDED','FAILED')),
    receipt text,
    error text,
    attempted_at timestamptz NOT NULL,
    CHECK ((status = 'SUCCEEDED') = (receipt IS NOT NULL)),
    CHECK ((status = 'FAILED') = (error IS NOT NULL))
);
CREATE TRIGGER delivery_audits_no_update_delete BEFORE UPDATE OR DELETE ON delivery_audits
FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER delivery_audits_no_truncate BEFORE TRUNCATE ON delivery_audits
FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();

INSERT INTO prompts(id,version,prompt_json,prompt_hash,created_at)
VALUES ('00000000-0000-0000-0000-000000000001','production-v1',
        '{"contract":"approved-v1","paper_trading_only":true,"provider":"copilot"}',
        canonical_jsonb_sha256('{"contract":"approved-v1","paper_trading_only":true,"provider":"copilot"}'),
        clock_timestamp());

CREATE OR REPLACE FUNCTION reserve_delivery(p_key text,p_hash sha256_hex,p_at timestamptz)
RETURNS delivery_status LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE current delivery_reservations%ROWTYPE;
BEGIN
  INSERT INTO delivery_reservations(delivery_key,content_hash,status,updated_at)
    VALUES(p_key,p_hash,'RESERVED',p_at) ON CONFLICT DO NOTHING;
  SELECT * INTO STRICT current FROM delivery_reservations WHERE delivery_key=p_key FOR UPDATE;
  IF current.content_hash<>p_hash THEN RAISE EXCEPTION 'delivery idempotency conflict'; END IF;
  IF current.status='FAILED' THEN
    UPDATE delivery_reservations SET status='RESERVED',last_error=NULL,updated_at=p_at WHERE delivery_key=p_key;
    RETURN 'RESERVED';
  END IF;
  RETURN current.status;
END $$;

CREATE OR REPLACE FUNCTION record_delivery(
 p_id uuid,p_key text,p_hash sha256_hex,p_status delivery_status,p_receipt text,p_error text,p_at timestamptz)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE current delivery_reservations%ROWTYPE;
BEGIN
  SELECT * INTO STRICT current FROM delivery_reservations WHERE delivery_key=p_key FOR UPDATE;
  IF current.content_hash<>p_hash OR current.status<>'RESERVED' OR p_status NOT IN ('SUCCEEDED','FAILED') THEN
    RAISE EXCEPTION 'invalid delivery completion';
  END IF;
  INSERT INTO delivery_audits VALUES(p_id,p_key,p_hash,p_status,p_receipt,p_error,p_at);
  UPDATE delivery_reservations SET status=p_status,receipt=p_receipt,last_error=p_error,updated_at=p_at
    WHERE delivery_key=p_key;
END $$;

CREATE OR REPLACE FUNCTION prestart_reset(p_event_id uuid,p_actor text,p_key text,p_request jsonb,p_hash sha256_hex,p_at timestamptz)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('lifecycle',0));
  IF (SELECT status FROM contest_state WHERE singleton FOR UPDATE)<>'DRAFT' THEN
    RAISE EXCEPTION 'reset is pre-start only';
  END IF;
  IF EXISTS (SELECT FROM orders) OR EXISTS (SELECT FROM agent_runs) THEN
    RAISE EXCEPTION 'reset refuses persisted run or order audit facts';
  END IF;
  DELETE FROM scheduled_agent_runs;
  DELETE FROM positions;
  UPDATE account_balances SET cash=30000,fee_tier='STARTER',stock_trade_count=0,updated_at=p_at;
  INSERT INTO contest_state_events VALUES(p_event_id,p_key,p_request,p_hash,'DRAFT','DRAFT','pre-start reset',p_actor,p_at);
  RETURN p_event_id;
END $$;

REVOKE ALL ON FUNCTION reserve_delivery(text,sha256_hex,timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION record_delivery(uuid,text,sha256_hex,delivery_status,text,text,timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION prestart_reset(uuid,text,text,jsonb,sha256_hex,timestamptz) FROM PUBLIC;
GRANT SELECT ON schema_migrations,delivery_reservations,delivery_audits TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION reserve_delivery(text,sha256_hex,timestamptz) TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION record_delivery(uuid,text,sha256_hex,delivery_status,text,text,timestamptz) TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION prestart_reset(uuid,text,text,jsonb,sha256_hex,timestamptz) TO ai_stocks_runtime;
RESET ROLE;
