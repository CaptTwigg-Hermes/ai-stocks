-- Crash-safe run claiming and Discord delivery leasing.
CREATE OR REPLACE FUNCTION claim_scheduled_run(p_now timestamptz,p_lease interval,p_token uuid)
RETURNS SETOF scheduled_agent_runs LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE claimed scheduled_agent_runs%ROWTYPE;
BEGIN
  IF NOT pg_try_advisory_xact_lock(hashtextextended('scheduled-runs',0)) THEN RETURN; END IF;
  SELECT * INTO claimed FROM scheduled_agent_runs
   WHERE (status='PENDING' AND (next_attempt_at<=p_now OR deadline_at<=p_now))
      OR (status='CLAIMED' AND lease_until<=p_now)
   ORDER BY (deadline_at<=p_now) DESC,scheduled_at,agent_id,id FOR UPDATE SKIP LOCKED LIMIT 1;
  IF NOT FOUND THEN RETURN; END IF;
  UPDATE scheduled_agent_runs SET status='CLAIMED',claim_token=p_token,lease_until=p_now+p_lease,
    attempt_count=attempt_count+1 WHERE id=claimed.id RETURNING * INTO claimed;
  RETURN NEXT claimed;
END $$;

ALTER TABLE delivery_reservations
  ADD COLUMN lease_token uuid,
  ADD COLUMN lease_until timestamptz,
  ADD COLUMN send_started boolean NOT NULL DEFAULT false;
UPDATE delivery_reservations
   SET lease_token=gen_random_uuid(),lease_until=updated_at
 WHERE status='RESERVED';

DROP FUNCTION reserve_delivery(text,sha256_hex,timestamptz);
CREATE FUNCTION reserve_delivery(p_key text,p_hash sha256_hex,p_at timestamptz,p_lease interval)
RETURNS TABLE(outcome text,lease_token uuid) LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE current delivery_reservations%ROWTYPE; new_token uuid:=gen_random_uuid();
BEGIN
  INSERT INTO delivery_reservations(delivery_key,content_hash,status,updated_at,lease_token,lease_until)
    VALUES(p_key,p_hash,'RESERVED',p_at,new_token,p_at+p_lease) ON CONFLICT DO NOTHING;
  SELECT * INTO STRICT current FROM delivery_reservations r WHERE r.delivery_key=p_key FOR UPDATE;
  IF current.content_hash<>p_hash THEN RAISE EXCEPTION 'delivery idempotency conflict'; END IF;
  IF current.status='SUCCEEDED' THEN RETURN QUERY SELECT 'SUCCEEDED'::text,NULL::uuid; RETURN; END IF;
  IF current.send_started THEN RETURN QUERY SELECT 'UNCERTAIN'::text,NULL::uuid; RETURN; END IF;
  IF current.lease_token=new_token THEN RETURN QUERY SELECT 'ACQUIRED'::text,new_token; RETURN; END IF;
  IF current.status='FAILED' OR current.lease_until<=p_at THEN
    UPDATE delivery_reservations SET status='RESERVED',last_error=NULL,updated_at=p_at,
      lease_token=new_token,lease_until=p_at+p_lease WHERE delivery_key=p_key;
    RETURN QUERY SELECT 'ACQUIRED'::text,new_token; RETURN;
  END IF;
  RETURN QUERY SELECT 'BUSY'::text,NULL::uuid;
END $$;

CREATE FUNCTION begin_delivery_send(p_key text,p_hash sha256_hex,p_token uuid,p_at timestamptz)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
BEGIN
  UPDATE delivery_reservations SET send_started=true,lease_until=NULL,updated_at=p_at
   WHERE delivery_key=p_key AND content_hash=p_hash AND status='RESERVED'
     AND lease_token=p_token AND NOT send_started AND lease_until>p_at;
  IF NOT FOUND THEN RAISE EXCEPTION 'delivery lease lost before send'; END IF;
END $$;

DROP FUNCTION record_delivery(uuid,text,sha256_hex,delivery_status,text,text,timestamptz);
CREATE FUNCTION record_delivery(
 p_id uuid,p_key text,p_hash sha256_hex,p_status delivery_status,p_receipt text,p_error text,p_at timestamptz,p_token uuid)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE current delivery_reservations%ROWTYPE;
BEGIN
  SELECT * INTO STRICT current FROM delivery_reservations WHERE delivery_key=p_key FOR UPDATE;
  IF current.content_hash<>p_hash OR current.status<>'RESERVED' OR NOT current.send_started
     OR current.lease_token<>p_token OR p_status NOT IN ('SUCCEEDED','FAILED') THEN
    RAISE EXCEPTION 'invalid delivery completion';
  END IF;
  INSERT INTO delivery_audits VALUES(p_id,p_key,p_hash,p_status,p_receipt,p_error,p_at);
  IF p_status='SUCCEEDED' THEN
    UPDATE delivery_reservations SET status='SUCCEEDED',receipt=p_receipt,last_error=NULL,
      updated_at=p_at,lease_token=NULL,lease_until=NULL WHERE delivery_key=p_key;
  ELSE
    -- An external send error is ambiguous. Keep send_started=true so retries fail closed.
    UPDATE delivery_reservations SET last_error=p_error,updated_at=p_at WHERE delivery_key=p_key;
  END IF;
END $$;

GRANT EXECUTE ON FUNCTION reserve_delivery(text,sha256_hex,timestamptz,interval) TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION begin_delivery_send(text,sha256_hex,uuid,timestamptz) TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION record_delivery(uuid,text,sha256_hex,delivery_status,text,text,timestamptz,uuid) TO ai_stocks_runtime;
