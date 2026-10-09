CREATE TABLE clinical.clinician_session (
    session_id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    clinician_id       uuid NOT NULL REFERENCES clinical.clinician (clinician_id),
    device_id          text NOT NULL REFERENCES clinical.device (device_id),
    refresh_token_hash bytea NOT NULL UNIQUE,        -- SHA-256, never the raw token
    issued_at          timestamptz NOT NULL DEFAULT now(),
    expires_at         timestamptz NOT NULL,
    revoked_at         timestamptz,
    last_seen_at       timestamptz
);

CREATE INDEX ix_clinician_session_active
    ON clinical.clinician_session (clinician_id) WHERE revoked_at IS NULL;
