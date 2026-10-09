CREATE TABLE clinical.clinician (
    clinician_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    username     text NOT NULL UNIQUE,
    full_name    text NOT NULL,
    role         text NOT NULL CHECK (role IN ('nurse', 'wound_specialist', 'admin')),
    facility_id  text NOT NULL REFERENCES clinical.facility (facility_id),
    active       boolean NOT NULL DEFAULT true,
    created_at   timestamptz NOT NULL DEFAULT now()
);

-- Kept apart from the identity row so it can be rotated, locked or purged on its own (§9.1).
CREATE TABLE clinical.clinician_credential (
    clinician_id         uuid PRIMARY KEY REFERENCES clinical.clinician (clinician_id),
    password_hash        text NOT NULL,              -- Argon2id, encoded with its parameters
    password_salt        bytea NOT NULL,
    mfa_secret_encrypted bytea,
    failed_attempts      integer NOT NULL DEFAULT 0,
    locked_until         timestamptz,
    password_updated_at  timestamptz NOT NULL DEFAULT now()
);
