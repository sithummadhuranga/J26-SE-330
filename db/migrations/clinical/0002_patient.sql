-- Minimal local patient record (§9.1). The sync path only ever sees patient_ref.
-- Created before wound so wound.patient_ref can reference it.
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE clinical.patient (
    patient_id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_ref             text NOT NULL UNIQUE,
    facility_id             text NOT NULL REFERENCES clinical.facility (facility_id),
    display_alias           text,      -- clinician-entered, e.g. initials or bed number
    mrn_reference_encrypted bytea,     -- optional, pgcrypto, facility opt-in
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now()
);
