-- Session families (architecture §13: "average session lifetime"). Refresh rotates the token by ending the session
-- row and issuing a new one, so a single login is a chain of rows. family_id ties the chain together: a login starts
-- a family, each refresh carries it on. Existing rows each become their own family.
-- The default keeps the previous service version working: its inserts get a new family each time (§9.2).
ALTER TABLE clinical.clinician_session ADD COLUMN family_id uuid NOT NULL DEFAULT gen_random_uuid();
UPDATE clinical.clinician_session SET family_id = session_id;

CREATE INDEX ix_clinician_session_family ON clinical.clinician_session (family_id, issued_at);
