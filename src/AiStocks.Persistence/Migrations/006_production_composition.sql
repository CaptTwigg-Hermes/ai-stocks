-- Production execution, reporting, and least-privilege composition.
CREATE TABLE daily_reports (
    report_key text PRIMARY KEY CHECK (length(report_key) BETWEEN 1 AND 256),
    trading_day date NOT NULL UNIQUE,
    generated_at timestamptz NOT NULL,
    content text NOT NULL CHECK (length(content) BETWEEN 1 AND 6000),
    content_hash sha256_hex NOT NULL CHECK (content_hash = encode(digest(content,'sha256'),'hex')),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TRIGGER daily_reports_no_update_delete BEFORE UPDATE OR DELETE ON daily_reports
FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER daily_reports_no_truncate BEFORE TRUNCATE ON daily_reports
FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();

CREATE TABLE daily_report_values (
    report_key text NOT NULL REFERENCES daily_reports(report_key) ON DELETE RESTRICT,
    agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
    net_value nonnegative_money NOT NULL,
    PRIMARY KEY (report_key,agent_id)
);
CREATE TRIGGER daily_report_values_no_update_delete BEFORE UPDATE OR DELETE ON daily_report_values
FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER daily_report_values_no_truncate BEFORE TRUNCATE ON daily_report_values
FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();

CREATE FUNCTION persist_daily_report(
 p_key text,p_day date,p_generated_at timestamptz,p_content text,p_hash sha256_hex)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE prior daily_reports%ROWTYPE;
BEGIN
  IF p_key<>'daily:'||to_char(p_day,'YYYY-MM-DD')
     OR p_generated_at<>(p_day::timestamp AT TIME ZONE 'Europe/Stockholm')+interval '18 hours 30 minutes'
     OR p_hash<>encode(digest(p_content,'sha256'),'hex') THEN
    RAISE EXCEPTION 'daily report identity is invalid';
  END IF;
  SELECT * INTO prior FROM daily_reports WHERE report_key=p_key FOR KEY SHARE;
  IF FOUND THEN
    IF prior.trading_day=p_day AND prior.generated_at=p_generated_at AND prior.content_hash=p_hash THEN RETURN; END IF;
    RAISE EXCEPTION 'daily report idempotency conflict';
  END IF;
  INSERT INTO daily_reports(report_key,trading_day,generated_at,content,content_hash)
  VALUES(p_key,p_day,p_generated_at,p_content,p_hash);
END $$;

CREATE FUNCTION persist_daily_report_value(p_key text,p_agent_id uuid,p_net_value numeric)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE prior numeric;
BEGIN
  IF p_net_value<0 OR NOT EXISTS (SELECT FROM daily_reports WHERE report_key=p_key) THEN
    RAISE EXCEPTION 'daily report value identity is invalid';
  END IF;
  SELECT net_value INTO prior FROM daily_report_values
    WHERE report_key=p_key AND agent_id=p_agent_id FOR KEY SHARE;
  IF FOUND THEN
    IF prior=p_net_value THEN RETURN; END IF;
    RAISE EXCEPTION 'daily report value idempotency conflict';
  END IF;
  INSERT INTO daily_report_values VALUES(p_key,p_agent_id,p_net_value);
END $$;

CREATE FUNCTION execute_queued_order(p_order_id uuid,p_now timestamptz)
RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE
  o orders%ROWTYPE; obs market_observations%ROWTYPE; s trading_sessions%ROWTYPE;
  derived_adv numeric; history_count integer; marked_capital numeric;
  active_tier fee_tier; trade_count integer; slip numeric; execution numeric; gross_value numeric; commission numeric;
  event_value jsonb; fill_id uuid:=gen_random_uuid();
BEGIN
  SELECT * INTO STRICT o FROM orders WHERE id=p_order_id;
  IF EXISTS (SELECT FROM order_outcomes WHERE order_id=o.id) THEN RETURN (SELECT id FROM fills WHERE order_id=o.id); END IF;
  IF EXISTS (SELECT FROM positions WHERE agent_id=o.agent_id AND instrument_id=o.instrument_id AND frozen) THEN
    RAISE EXCEPTION 'frozen positions are ineligible for execution';
  END IF;
  obs.id:=NULL;
  FOR obs IN SELECT mo.* FROM market_observations mo
    JOIN trading_sessions ts ON ts.session_id=mo.session_id
    WHERE mo.instrument_id=o.instrument_id AND mo.verified AND NOT mo.warning AND NOT mo.suspended
      AND mo.traded_at>=o.decision_at AND mo.traded_at BETWEEN ts.opens_at AND ts.closes_at
      AND mo.retrieved_at-mo.traded_at BETWEEN interval '15 minutes' AND interval '20 minutes'
      AND mo.retrieved_at<=p_now
      AND NOT EXISTS (SELECT 1 FROM contest_state_events pause_event
        WHERE pause_event.to_status='PAUSED' AND mo.traded_at>=pause_event.occurred_at
          AND mo.traded_at<COALESCE((SELECT min(resume_event.occurred_at) FROM contest_state_events resume_event
            WHERE resume_event.from_status='PAUSED' AND resume_event.to_status='RUNNING'
              AND resume_event.occurred_at>pause_event.occurred_at),'infinity'::timestamptz))
    ORDER BY mo.traded_at,mo.id
  LOOP
    SELECT * INTO STRICT s FROM trading_sessions WHERE session_id=obs.session_id;
    SELECT count(*),avg(stats.traded_value) INTO history_count,derived_adv
    FROM (SELECT session_id FROM trading_sessions WHERE session_day<s.session_day ORDER BY session_day DESC LIMIT 20) required
    JOIN instrument_session_stats stats ON stats.session_id=required.session_id
      AND stats.instrument_id=o.instrument_id AND stats.complete;
    EXIT WHEN o.side='SELL' OR (history_count=20 AND obs.complete_history_sessions>=20
      AND obs.average_daily_value_20=round(derived_adv,2) AND obs.price*o.quantity<=derived_adv*0.01);
    obs.id:=NULL;
  END LOOP;
  IF obs.id IS NULL THEN RETURN NULL; END IF;

  SELECT b.cash+COALESCE(sum(p.quantity*mark.price),0),b.fee_tier,b.stock_trade_count
    INTO marked_capital,active_tier,trade_count
  FROM account_balances b
  LEFT JOIN positions p ON p.agent_id=b.agent_id AND p.quantity>0
  LEFT JOIN LATERAL (
    SELECT mo.price FROM market_observations mo JOIN trading_sessions ms ON ms.session_id=mo.session_id
    WHERE mo.instrument_id=p.instrument_id AND mo.verified AND NOT mo.warning AND NOT mo.suspended
      AND mo.session_id=obs.session_id AND mo.traded_at BETWEEN ms.opens_at AND ms.closes_at
      AND mo.traded_at<=obs.traded_at AND obs.traded_at-mo.traded_at<=interval '20 minutes'
      AND mo.retrieved_at-mo.traded_at BETWEEN interval '15 minutes' AND interval '20 minutes'
      AND mo.retrieved_at<=obs.retrieved_at ORDER BY mo.traded_at DESC,mo.id DESC LIMIT 1) mark ON true
  WHERE b.agent_id=o.agent_id GROUP BY b.cash,b.fee_tier,b.stock_trade_count;
  IF marked_capital IS NULL THEN RAISE EXCEPTION 'authoritative account state is unavailable'; END IF;
  IF active_tier='MINI' OR marked_capital>=50000 OR trade_count>=500 THEN active_tier:='MINI'; END IF;
  derived_adv:=CASE WHEN o.side='BUY' THEN derived_adv ELSE obs.average_daily_value_20 END;
  slip:=round(least(0.01,greatest(0.001,
    CASE WHEN obs.bid IS NOT NULL AND obs.ask IS NOT NULL THEN (obs.ask-obs.bid)/(2*obs.price) ELSE 0.001 END)
    +0.0025*sqrt((obs.price*o.quantity/derived_adv)::numeric)),8);
  execution:=round(obs.price*(CASE WHEN o.side='BUY' THEN 1+slip ELSE 1-slip END),4);
  gross_value:=round(execution*o.quantity,2);
  commission:=CASE WHEN active_tier='STARTER' THEN 0 ELSE greatest(1,round(gross_value*0.0025,2)) END;
  event_value:=jsonb_build_object('kind','FILL','order_id',o.id,'agent_id',o.agent_id,
    'observation_id',obs.id,'execution_price',execution,'gross',gross_value,'fee',commission,'slippage',slip);
  RETURN record_fill(fill_id,gen_random_uuid(),gen_random_uuid(),o.id,o.agent_id,obs.id,o.quantity,
    execution,gross_value,commission,slip,obs.retrieved_at,event_value,canonical_jsonb_sha256(event_value),
    event_value,canonical_jsonb_sha256(event_value),event_value,canonical_jsonb_sha256(event_value),'fill:'||o.id);
END $$;

DO $roles$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='ai_stocks_worker_runtime') THEN CREATE ROLE ai_stocks_worker_runtime NOLOGIN; END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='ai_stocks_operations_runtime') THEN CREATE ROLE ai_stocks_operations_runtime NOLOGIN; END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='ai_stocks_web_runtime') THEN CREATE ROLE ai_stocks_web_runtime NOLOGIN; END IF;
END $roles$;

REVOKE ALL ON daily_reports,daily_report_values FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION persist_daily_report(text,date,timestamptz,text,sha256_hex) FROM PUBLIC,ai_stocks_runtime;
REVOKE EXECUTE ON FUNCTION persist_daily_report_value(text,uuid,numeric) FROM PUBLIC,ai_stocks_runtime;
REVOKE EXECUTE ON FUNCTION execute_queued_order(uuid,timestamptz) FROM PUBLIC,ai_stocks_runtime;
REVOKE EXECUTE ON FUNCTION apply_corporate_action(uuid,uuid,uuid,uuid,timestamptz) FROM ai_stocks_runtime;
REVOKE EXECUTE ON FUNCTION finalize_contest(text,uuid,text,jsonb,sha256_hex,timestamptz) FROM ai_stocks_runtime;
REVOKE EXECUTE ON FUNCTION reserve_delivery(text,sha256_hex,timestamptz,interval) FROM ai_stocks_runtime;
REVOKE EXECUTE ON FUNCTION begin_delivery_send(text,sha256_hex,uuid,timestamptz) FROM ai_stocks_runtime;
REVOKE EXECUTE ON FUNCTION record_delivery(uuid,text,sha256_hex,delivery_status,text,text,timestamptz,uuid) FROM ai_stocks_runtime;

GRANT USAGE ON SCHEMA public TO ai_stocks_worker_runtime,ai_stocks_operations_runtime,ai_stocks_web_runtime;
GRANT SELECT ON agents,contest_state,instruments,trading_sessions,instrument_session_stats,market_observations,prompts,
 scheduled_agent_runs,agent_runs,orders,order_outcomes,account_balances,positions,schema_migrations,research_attestations TO ai_stocks_worker_runtime;
GRANT INSERT ON scheduled_agent_runs,agent_runs TO ai_stocks_worker_runtime;
GRANT EXECUTE ON FUNCTION submit_order(uuid,uuid,text,text,order_side,uuid,integer,timestamptz,jsonb,sha256_hex),
 claim_scheduled_run(timestamptz,interval,uuid),complete_scheduled_run(uuid,uuid,run_status,timestamptz,text,timestamptz),
 execute_queued_order(uuid,timestamptz),
 persist_research_attestation(uuid,uuid,uuid,uuid,text,text,text,text,jsonb,sha256_hex,bytea,sha256_hex,jsonb,sha256_hex,timestamptz)
 TO ai_stocks_worker_runtime;

GRANT SELECT ON agents,contest_state,instruments,trading_sessions,market_observations,scheduled_agent_runs,agent_runs,
 orders,order_outcomes,ledger_events,fills,corporate_actions,corporate_action_applications,account_balances,positions,
 portfolio_snapshots,final_rankings,daily_reports,daily_report_values,delivery_reservations,delivery_audits TO ai_stocks_operations_runtime;
GRANT EXECUTE ON FUNCTION apply_corporate_action(uuid,uuid,uuid,uuid,timestamptz),
 finalize_contest(text,uuid,text,jsonb,sha256_hex,timestamptz),persist_daily_report(text,date,timestamptz,text,sha256_hex),
 persist_daily_report_value(text,uuid,numeric),
 reserve_delivery(text,sha256_hex,timestamptz,interval),begin_delivery_send(text,sha256_hex,uuid,timestamptz),
 record_delivery(uuid,text,sha256_hex,delivery_status,text,text,timestamptz,uuid) TO ai_stocks_operations_runtime;

GRANT SELECT ON agents,contest_state,instruments,trading_sessions,orders,order_outcomes,ledger_events,fills,
 corporate_actions,corporate_action_applications,account_balances,positions,portfolio_snapshots,final_rankings,
 contest_state_events,daily_reports,daily_report_values TO ai_stocks_web_runtime;
GRANT EXECUTE ON FUNCTION transition_contest(uuid,contest_status,contest_status,text,text,text,jsonb,sha256_hex,timestamptz),
 prestart_reset(uuid,text,text,jsonb,sha256_hex,timestamptz) TO ai_stocks_web_runtime;

DO $memberships$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='ai_stocks_worker') OR
     pg_has_role('ai_stocks_worker','ai_stocks_runtime','member') OR
     NOT pg_has_role('ai_stocks_worker','ai_stocks_worker_runtime','member') THEN
    RAISE EXCEPTION 'ai_stocks_worker role memberships are not preprovisioned correctly' USING ERRCODE='42501';
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='ai_stocks_operations') OR
     pg_has_role('ai_stocks_operations','ai_stocks_runtime','member') OR
     NOT pg_has_role('ai_stocks_operations','ai_stocks_operations_runtime','member') THEN
    RAISE EXCEPTION 'ai_stocks_operations role memberships are not preprovisioned correctly' USING ERRCODE='42501';
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='ai_stocks_web') OR
     pg_has_role('ai_stocks_web','ai_stocks_runtime','member') OR
     NOT pg_has_role('ai_stocks_web','ai_stocks_web_runtime','member') THEN
    RAISE EXCEPTION 'ai_stocks_web role memberships are not preprovisioned correctly' USING ERRCODE='42501';
  END IF;
END $memberships$;
