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
    AND NOT EXISTS (SELECT 1 FROM contest_state_events pause_event
      WHERE pause_event.to_status='PAUSED' AND mo.traded_at>=pause_event.occurred_at
        AND mo.traded_at<COALESCE((SELECT min(resume_event.occurred_at) FROM contest_state_events resume_event
          WHERE resume_event.from_status='PAUSED' AND resume_event.to_status='RUNNING'
            AND resume_event.occurred_at>pause_event.occurred_at),'infinity'::timestamptz))
  ORDER BY mo.traded_at DESC,mo.id DESC LIMIT 1;
  IF authoritative_price IS NULL THEN RAISE EXCEPTION 'latest verified pre-decision observation is required'; END IF;
  INSERT INTO orders(id,agent_id,decision_id,idempotency_key,side,instrument_id,quantity,
                     decision_at,observed_price,request_json,request_hash)
    VALUES(p_id,p_agent_id,p_decision_id,p_idempotency_key,p_side,p_instrument_id,p_quantity,
           p_decision_at,authoritative_price,p_request_json,p_request_hash);
  RETURN p_id;
END $$;

REVOKE EXECUTE ON FUNCTION cancel_order(uuid,uuid,uuid,text,jsonb,sha256_hex,timestamptz)
  FROM PUBLIC,ai_stocks_runtime,ai_stocks_operations_runtime,ai_stocks_web_runtime;
GRANT EXECUTE ON FUNCTION cancel_order(uuid,uuid,uuid,text,jsonb,sha256_hex,timestamptz)
  TO ai_stocks_worker_runtime;
