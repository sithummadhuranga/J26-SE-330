CREATE SCHEMA IF NOT EXISTS clinical;

CREATE TABLE clinical.facility (
    facility_id text PRIMARY KEY,
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE clinical.device (
    device_id     text PRIMARY KEY,
    facility_id   text NOT NULL REFERENCES clinical.facility (facility_id),
    registered_at timestamptz NOT NULL DEFAULT now(),
    revoked_at    timestamptz
);
