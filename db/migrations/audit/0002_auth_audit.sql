CREATE TABLE audit.auth_audit (
    auth_audit_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username      text NOT NULL,
    clinician_id  uuid,
    device_id     text,
    action        text NOT NULL CHECK (action IN ('LOGIN', 'REFRESH', 'LOGOUT', 'LOCKOUT')),
    success       boolean NOT NULL,
    reason_code   text,
    recorded_at   timestamptz NOT NULL DEFAULT now()
);
