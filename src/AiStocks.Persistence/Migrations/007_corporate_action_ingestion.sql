-- Collector-only immutable ingestion of approved, evidence-backed corporate actions.
SET LOCAL ROLE ai_stocks_migrator;

CREATE TABLE corporate_action_ingestion_artifacts (
    external_reference text PRIMARY KEY,
    action_id uuid NOT NULL UNIQUE REFERENCES corporate_actions(id) ON DELETE RESTRICT,
    input_payload bytea NOT NULL CHECK (octet_length(input_payload) BETWEEN 2 AND 1048576),
    input_sha256 sha256_hex NOT NULL UNIQUE CHECK (input_sha256 = encode(digest(input_payload,'sha256'),'hex')),
    input_json jsonb NOT NULL,
    ingested_at timestamptz NOT NULL,
    CHECK (convert_from(input_payload,'UTF8')::jsonb = input_json),
    CHECK (input_json->>'externalReference' = external_reference),
    CHECK ((input_json->>'id')::uuid = action_id)
);
CREATE TRIGGER corporate_action_ingestion_artifacts_immutable
BEFORE UPDATE OR DELETE ON corporate_action_ingestion_artifacts
FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER corporate_action_ingestion_artifacts_no_truncate
BEFORE TRUNCATE ON corporate_action_ingestion_artifacts
FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();

CREATE FUNCTION ingest_verified_corporate_action(
 p_input jsonb,p_payload bytea,p_payload_hash sha256_hex,p_ingested_at timestamptz)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE
  action_id uuid;
  instrument_id uuid;
  v_external_reference text;
  action_type text;
  effective_at timestamptz;
  approved_at timestamptz;
  normalized jsonb;
  primary_evidence jsonb;
  secondary_evidence jsonb;
  existing corporate_action_ingestion_artifacts%ROWTYPE;
BEGIN
  IF encode(digest(p_payload,'sha256'),'hex')<>p_payload_hash
     OR convert_from(p_payload,'UTF8')::jsonb<>p_input
     OR jsonb_typeof(p_input)<>'object'
     OR p_input->>'schemaVersion'<>'1' THEN
    RAISE EXCEPTION 'corporate action input identity is invalid';
  END IF;

  action_id:=(p_input->>'id')::uuid;
  v_external_reference:=p_input->>'externalReference';
  action_type:=p_input->>'actionType';
  effective_at:=(p_input->>'effectiveAt')::timestamptz;
  approved_at:=(p_input#>>'{approval,approvedAt}')::timestamptz;
  normalized:=p_input->'normalized';
  primary_evidence:=p_input->'primaryEvidence';
  secondary_evidence:=p_input->'secondaryEvidence';

  SELECT * INTO existing FROM corporate_action_ingestion_artifacts
   WHERE corporate_action_ingestion_artifacts.external_reference=v_external_reference FOR KEY SHARE;
  IF FOUND THEN
    IF existing.action_id=action_id AND existing.input_sha256=p_payload_hash THEN RETURN action_id; END IF;
    RAISE EXCEPTION 'corporate action ingestion idempotency conflict';
  END IF;

  IF v_external_reference IS NULL OR length(v_external_reference) NOT BETWEEN 1 AND 300
     OR action_type NOT IN ('DIVIDEND','SPLIT','CASH_MERGER','STOCK_MERGER','DELISTING','CORRECTION')
     OR jsonb_typeof(normalized)<>'object'
     OR jsonb_typeof(primary_evidence)<>'object'
     OR jsonb_typeof(secondary_evidence)<>'object'
     OR coalesce(p_input#>>'{approval,approvedBy}','') !~ '^owner:[A-Za-z0-9._@+-]{1,200}$'
     OR approved_at>p_ingested_at THEN
    RAISE EXCEPTION 'corporate action normalization or approval is invalid';
  END IF;

  IF primary_evidence->>'authority'<>'nasdaq-main-market-notices'
     OR primary_evidence->>'sourceUrl' NOT LIKE 'https://api.news.eu.nasdaq.com/%'
     OR coalesce(primary_evidence->>'payloadSha256','') !~ '^[0-9a-f]{64}$'
     OR coalesce(secondary_evidence->>'authority','') IN ('','nasdaq-main-market-notices')
     OR coalesce(secondary_evidence->>'sourceUrl','') !~ '^https://'
     OR coalesce(secondary_evidence->>'payloadSha256','') !~ '^[0-9a-f]{64}$'
     OR (primary_evidence->>'publishedAt')::timestamptz>(primary_evidence->>'retrievedAt')::timestamptz
     OR (secondary_evidence->>'publishedAt')::timestamptz>(secondary_evidence->>'retrievedAt')::timestamptz
     OR (primary_evidence->>'retrievedAt')::timestamptz>approved_at
     OR (secondary_evidence->>'retrievedAt')::timestamptz>approved_at THEN
    RAISE EXCEPTION 'corporate action evidence is not independently verified';
  END IF;

  SELECT id INTO STRICT instrument_id FROM instruments
   WHERE isin=p_input->>'isin' AND order_book_id=p_input->>'orderBookId' AND mic='XSTO';

  IF (action_type='DIVIDEND' AND
        ((normalized->>'per_share')::numeric<0
         OR (normalized->>'ownership_close')::timestamptz >= (normalized->>'ex_date')::date
         OR (normalized->>'payment_at')::timestamptz<>effective_at))
     OR (action_type='SPLIT' AND
        ((normalized->>'numerator')::integer<=0 OR (normalized->>'denominator')::integer<=0))
     OR (action_type='CASH_MERGER' AND (normalized->>'per_share')::numeric<0)
     OR (action_type='STOCK_MERGER' AND
        ((normalized->>'target_instrument_id')::uuid=instrument_id
         OR (normalized->>'numerator')::integer<=0 OR (normalized->>'denominator')::integer<=0
         OR NOT EXISTS (SELECT FROM instruments WHERE id=(normalized->>'target_instrument_id')::uuid)))
     OR (action_type='DELISTING' AND normalized ? 'official_proceeds'
         AND (normalized->>'official_proceeds')::numeric<0)
     OR (action_type='CORRECTION' AND
         NOT (normalized ? 'cash_delta' OR normalized ? 'quantity_delta' OR normalized ? 'average_cost_after')) THEN
    RAISE EXCEPTION 'corporate action normalized values are invalid';
  END IF;

  INSERT INTO corporate_actions(id,external_reference,instrument_id,action_type,effective_at,
    normalized_json,normalized_hash,primary_evidence_json,primary_evidence_hash,
    secondary_evidence_json,secondary_evidence_hash,approved_by,created_at)
  VALUES(action_id,v_external_reference,instrument_id,action_type::corporate_action_type,effective_at,
    normalized,canonical_jsonb_sha256(normalized),primary_evidence,canonical_jsonb_sha256(primary_evidence),
    secondary_evidence,canonical_jsonb_sha256(secondary_evidence),p_input#>>'{approval,approvedBy}',approved_at);
  INSERT INTO corporate_action_ingestion_artifacts
    (external_reference,action_id,input_payload,input_sha256,input_json,ingested_at)
  VALUES(v_external_reference,action_id,p_payload,p_payload_hash,p_input,p_ingested_at);
  RETURN action_id;
END $$;

REVOKE ALL ON corporate_action_ingestion_artifacts FROM PUBLIC,ai_stocks_runtime,ai_stocks_collector;
REVOKE ALL ON FUNCTION ingest_verified_corporate_action(jsonb,bytea,sha256_hex,timestamptz)
FROM PUBLIC,ai_stocks_runtime;
GRANT SELECT ON corporate_action_ingestion_artifacts TO ai_stocks_collector,ai_stocks_operations_runtime;
GRANT EXECUTE ON FUNCTION ingest_verified_corporate_action(jsonb,bytea,sha256_hex,timestamptz)
TO ai_stocks_collector;

CREATE TABLE immediate_alerts (
    alert_key text PRIMARY KEY CHECK (length(alert_key) BETWEEN 1 AND 240),
    kind text NOT NULL CHECK (kind IN
      ('SystemPause','RunWideInvalidMarketData','DatabaseOrBackupFailure','MultiModelAuthenticationOutage','AccountingInvariantViolation')),
    detail text NOT NULL CHECK (length(detail) BETWEEN 1 AND 1000),
    message text NOT NULL CHECK (length(message) BETWEEN 1 AND 6000),
    content_hash sha256_hex NOT NULL CHECK (content_hash=encode(digest(message,'sha256'),'hex')),
    occurred_at timestamptz NOT NULL
);
CREATE TRIGGER immediate_alerts_immutable BEFORE UPDATE OR DELETE ON immediate_alerts
FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER immediate_alerts_no_truncate BEFORE TRUNCATE ON immediate_alerts
FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();

CREATE FUNCTION enqueue_immediate_alert(p_kind text,p_detail text,p_key text,p_at timestamptz)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE
  v_message text;
  existing immediate_alerts%ROWTYPE;
BEGIN
  IF p_kind NOT IN ('SystemPause','RunWideInvalidMarketData','DatabaseOrBackupFailure','MultiModelAuthenticationOutage','AccountingInvariantViolation')
     OR length(trim(p_detail)) NOT BETWEEN 1 AND 1000 OR length(trim(p_key)) NOT BETWEEN 1 AND 240 THEN
    RAISE EXCEPTION 'immediate alert identity is invalid';
  END IF;
  v_message:='🚨 '||p_kind||': '||trim(p_detail);
  SELECT * INTO existing FROM immediate_alerts WHERE alert_key=trim(p_key) FOR KEY SHARE;
  IF FOUND THEN
    IF existing.kind=p_kind AND existing.detail=trim(p_detail)
       AND existing.content_hash=encode(digest(v_message,'sha256'),'hex') THEN RETURN existing.alert_key; END IF;
    RAISE EXCEPTION 'immediate alert idempotency conflict';
  END IF;
  INSERT INTO immediate_alerts(alert_key,kind,detail,message,content_hash,occurred_at)
  VALUES(trim(p_key),p_kind,trim(p_detail),v_message,encode(digest(v_message,'sha256'),'hex'),p_at);
  RETURN trim(p_key);
END $$;

CREATE FUNCTION enqueue_pause_alert() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
BEGIN
  IF NEW.to_status='PAUSED' THEN
    PERFORM enqueue_immediate_alert('SystemPause',NEW.reason,'pause:'||NEW.id,NEW.occurred_at);
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER contest_pause_immediate_alert AFTER INSERT ON contest_state_events
FOR EACH ROW EXECUTE FUNCTION enqueue_pause_alert();

CREATE FUNCTION enqueue_multi_model_auth_alert() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE affected integer;
BEGIN
  IF NEW.status IN ('FAILED','MISSED') AND lower(coalesce(NEW.audit_json->>'reason','')) LIKE '%auth%' THEN
    SELECT count(DISTINCT run.agent_id) INTO affected FROM agent_runs run
     WHERE run.status IN ('FAILED','MISSED')
       AND lower(coalesce(run.audit_json->>'reason','')) LIKE '%auth%'
       AND run.ended_at BETWEEN NEW.ended_at-interval '15 minutes' AND NEW.ended_at;
    IF affected=4 THEN
      PERFORM enqueue_immediate_alert('MultiModelAuthenticationOutage',
        'authentication failed for all four fixed models',
        'multi-model-auth:'||to_char(date_trunc('minute',NEW.ended_at AT TIME ZONE 'UTC'),'YYYYMMDDHH24MI'),NEW.ended_at);
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER agent_runs_multi_model_auth_alert AFTER INSERT ON agent_runs
FOR EACH ROW EXECUTE FUNCTION enqueue_multi_model_auth_alert();

REVOKE ALL ON immediate_alerts FROM PUBLIC,ai_stocks_runtime,ai_stocks_collector,
 ai_stocks_worker_runtime,ai_stocks_operations_runtime,ai_stocks_web_runtime;
REVOKE ALL ON FUNCTION enqueue_immediate_alert(text,text,text,timestamptz) FROM PUBLIC,ai_stocks_runtime;
REVOKE ALL ON FUNCTION enqueue_pause_alert() FROM PUBLIC,ai_stocks_runtime;
REVOKE ALL ON FUNCTION enqueue_multi_model_auth_alert() FROM PUBLIC,ai_stocks_runtime;
GRANT SELECT ON immediate_alerts TO ai_stocks_operations_runtime;
GRANT EXECUTE ON FUNCTION enqueue_immediate_alert(text,text,text,timestamptz)
TO ai_stocks_collector,ai_stocks_worker_runtime,ai_stocks_operations_runtime;
DO $backup_alert$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname='ai_stocks_backup') THEN
    GRANT EXECUTE ON FUNCTION enqueue_immediate_alert(text,text,text,timestamptz) TO ai_stocks_backup;
  END IF;
END $backup_alert$;
RESET ROLE;
