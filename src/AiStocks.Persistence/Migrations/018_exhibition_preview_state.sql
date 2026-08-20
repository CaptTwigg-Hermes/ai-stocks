CREATE TABLE exhibition_preview_state (
  singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
  revision bigint NOT NULL CHECK (revision > 0),
  state_json jsonb NOT NULL CHECK (
    jsonb_typeof(state_json) = 'object' AND octet_length(state_json::text) <= 4194304
  ),
  updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE exhibition_preview_mutation_receipts (
  mutation_id uuid PRIMARY KEY,
  state_revision bigint NOT NULL CHECK (state_revision > 0),
  committed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE FUNCTION bound_exhibition_preview_mutation_receipts()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $$
BEGIN
  DELETE FROM public.exhibition_preview_mutation_receipts
  WHERE state_revision <= NEW.state_revision - 100000;
  RETURN NULL;
END;
$$;

REVOKE ALL ON FUNCTION bound_exhibition_preview_mutation_receipts() FROM PUBLIC;
CREATE TRIGGER exhibition_preview_receipt_bound
AFTER INSERT ON exhibition_preview_mutation_receipts
FOR EACH ROW EXECUTE FUNCTION bound_exhibition_preview_mutation_receipts();

REVOKE ALL ON exhibition_preview_state FROM PUBLIC;
REVOKE ALL ON exhibition_preview_mutation_receipts FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON exhibition_preview_state TO ai_stocks_web_runtime;
GRANT SELECT, INSERT ON exhibition_preview_mutation_receipts TO ai_stocks_web_runtime;
