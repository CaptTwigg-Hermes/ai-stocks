ALTER TABLE exhibition_preview_state
  DROP CONSTRAINT exhibition_preview_state_pkey,
  DROP CONSTRAINT exhibition_preview_state_singleton_check;

ALTER TABLE exhibition_preview_state
  ADD COLUMN state_key text;

UPDATE exhibition_preview_state
SET state_key = 'official-nasdaq-xsto-15m-delayed';

ALTER TABLE exhibition_preview_state
  ALTER COLUMN state_key SET NOT NULL,
  DROP COLUMN singleton,
  ADD CONSTRAINT exhibition_preview_state_pkey PRIMARY KEY (state_key),
  ADD CONSTRAINT exhibition_preview_state_key_bounded
    CHECK (length(state_key) BETWEEN 1 AND 100);

ALTER TABLE exhibition_preview_mutation_receipts
  ADD COLUMN state_key text;

UPDATE exhibition_preview_mutation_receipts
SET state_key = 'official-nasdaq-xsto-15m-delayed';

ALTER TABLE exhibition_preview_mutation_receipts
  ALTER COLUMN state_key SET NOT NULL,
  ADD CONSTRAINT exhibition_preview_receipt_state_key_bounded
    CHECK (length(state_key) BETWEEN 1 AND 100);

CREATE OR REPLACE FUNCTION bound_exhibition_preview_mutation_receipts()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $$
BEGIN
  DELETE FROM public.exhibition_preview_mutation_receipts
  WHERE state_key = NEW.state_key
    AND state_revision <= NEW.state_revision - 100000;
  RETURN NULL;
END;
$$;
