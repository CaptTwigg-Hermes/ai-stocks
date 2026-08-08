-- Immutable research invocation/evidence attestation, persistable in the same
-- transaction as the associated run and paper order.
SET LOCAL ROLE ai_stocks_migrator;

CREATE TABLE research_attestations (
    id uuid PRIMARY KEY,
    agent_run_id uuid REFERENCES agent_runs(id) ON DELETE RESTRICT,
    order_id uuid REFERENCES orders(id) ON DELETE RESTRICT,
    agent_id uuid NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
    requested_model_id text NOT NULL,
    requested_provider text NOT NULL CHECK (requested_provider = 'copilot'),
    actual_model_id text NOT NULL,
    actual_provider text NOT NULL CHECK (actual_provider = 'copilot'),
    invocation_json jsonb NOT NULL,
    invocation_hash sha256_hex NOT NULL CHECK (invocation_hash = canonical_jsonb_sha256(invocation_json)),
    runtime_report bytea NOT NULL CHECK (octet_length(runtime_report) > 0),
    runtime_report_hash sha256_hex NOT NULL
        CHECK (runtime_report_hash = encode(digest(runtime_report, 'sha256'), 'hex')::sha256_hex),
    evidence_json jsonb NOT NULL CHECK (jsonb_typeof(evidence_json) = 'array'),
    evidence_hash sha256_hex NOT NULL CHECK (evidence_hash = canonical_jsonb_sha256(evidence_json)),
    attested_at timestamptz NOT NULL,
    CHECK (agent_run_id IS NOT NULL OR order_id IS NOT NULL),
    CHECK (requested_model_id = actual_model_id),
    CHECK (invocation_json->>'agent_id' = agent_id::text),
    CHECK (invocation_json->>'requested_model_id' = requested_model_id),
    CHECK (invocation_json->>'requested_provider' = requested_provider),
    CHECK (invocation_json->>'model_id' = actual_model_id),
    CHECK (invocation_json->>'provider' = actual_provider),
    CHECK (invocation_json->>'runtime_report_sha256' = runtime_report_hash::text),
    UNIQUE (agent_run_id),
    UNIQUE (order_id)
);

CREATE TRIGGER research_attestations_no_update_delete BEFORE UPDATE OR DELETE ON research_attestations
FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
CREATE TRIGGER research_attestations_no_truncate BEFORE TRUNCATE ON research_attestations
FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();

CREATE OR REPLACE FUNCTION persist_research_attestation(
    p_id uuid, p_agent_run_id uuid, p_order_id uuid, p_agent_id uuid,
    p_requested_model text, p_requested_provider text, p_actual_model text, p_actual_provider text,
    p_invocation jsonb, p_invocation_hash sha256_hex, p_runtime_report bytea,
    p_runtime_report_hash sha256_hex, p_evidence jsonb, p_evidence_hash sha256_hex,
    p_attested_at timestamptz
) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE existing research_attestations%ROWTYPE; matching_rows integer;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(p_agent_id::text,0));
    IF p_agent_run_id IS NULL AND p_order_id IS NULL THEN
        RAISE EXCEPTION 'attestation must bind a run or order';
    END IF;
    IF p_requested_model <> p_actual_model OR p_requested_provider <> 'copilot' OR p_actual_provider <> 'copilot' THEN
        RAISE EXCEPTION 'runtime model/provider attestation mismatch';
    END IF;
    IF p_invocation_hash <> canonical_jsonb_sha256(p_invocation)
       OR p_runtime_report_hash <> encode(digest(p_runtime_report, 'sha256'), 'hex')::sha256_hex
       OR p_evidence_hash <> canonical_jsonb_sha256(p_evidence) THEN
        RAISE EXCEPTION 'attestation hash mismatch';
    END IF;
    IF p_agent_run_id IS NOT NULL THEN
        PERFORM 1 FROM agent_runs WHERE id=p_agent_run_id AND agent_id=p_agent_id AND model_id=p_actual_model FOR KEY SHARE;
        IF NOT FOUND THEN RAISE EXCEPTION 'attestation run identity mismatch'; END IF;
    END IF;
    IF p_order_id IS NOT NULL THEN
        PERFORM 1 FROM orders WHERE id=p_order_id AND agent_id=p_agent_id FOR KEY SHARE;
        IF NOT FOUND THEN RAISE EXCEPTION 'attestation order identity mismatch'; END IF;
    END IF;
    SELECT count(*) INTO matching_rows FROM research_attestations
      WHERE (p_agent_run_id IS NOT NULL AND agent_run_id=p_agent_run_id)
         OR (p_order_id IS NOT NULL AND order_id=p_order_id);
    IF matching_rows > 1 THEN
        RAISE EXCEPTION 'split immutable research attestation identity';
    ELSIF matching_rows = 1 THEN
        SELECT * INTO STRICT existing FROM research_attestations
          WHERE (p_agent_run_id IS NOT NULL AND agent_run_id=p_agent_run_id)
             OR (p_order_id IS NOT NULL AND order_id=p_order_id) FOR KEY SHARE;
        IF existing.invocation_hash=p_invocation_hash AND existing.runtime_report_hash=p_runtime_report_hash
           AND existing.evidence_hash=p_evidence_hash THEN RETURN existing.id; END IF;
        RAISE EXCEPTION 'conflicting immutable research attestation';
    END IF;
    INSERT INTO research_attestations VALUES (
      p_id,p_agent_run_id,p_order_id,p_agent_id,p_requested_model,p_requested_provider,
      p_actual_model,p_actual_provider,p_invocation,p_invocation_hash,p_runtime_report,
      p_runtime_report_hash,p_evidence,p_evidence_hash,p_attested_at);
    RETURN p_id;
END $$;

GRANT SELECT ON research_attestations TO ai_stocks_runtime;
GRANT EXECUTE ON FUNCTION persist_research_attestation(uuid,uuid,uuid,uuid,text,text,text,text,jsonb,sha256_hex,bytea,sha256_hex,jsonb,sha256_hex,timestamptz) TO ai_stocks_runtime;
RESET ROLE;
