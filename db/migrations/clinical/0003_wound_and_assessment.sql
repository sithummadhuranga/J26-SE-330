CREATE TABLE clinical.wound (
    wound_id    uuid PRIMARY KEY,
    patient_ref text NOT NULL REFERENCES clinical.patient (patient_ref),
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- Append-only: an edit is a new revision, never an UPDATE (§3, §5).
CREATE TABLE clinical.wound_assessment (
    event_id            uuid PRIMARY KEY,               -- idempotency key
    assessment_id       uuid NOT NULL,
    revision            integer NOT NULL CHECK (revision >= 1),
    wound_id            uuid NOT NULL REFERENCES clinical.wound (wound_id),
    patient_ref         text NOT NULL,
    device_id           text NOT NULL REFERENCES clinical.device (device_id),
    captured_at         timestamptz NOT NULL,           -- device clock, untrusted for ordering
    received_at         timestamptz NOT NULL DEFAULT now(),
    analytics           jsonb NOT NULL,
    clinical_assessment jsonb NOT NULL,
    status              text NOT NULL DEFAULT 'PERSISTED'
                        CHECK (status IN ('PERSISTED', 'SUPERSEDED')),
    kafka_topic         text NOT NULL,
    kafka_partition     integer NOT NULL,
    kafka_offset        bigint NOT NULL,
    CONSTRAINT uq_assessment_revision UNIQUE (assessment_id, revision)
);

CREATE INDEX ix_wound_assessment_wound ON clinical.wound_assessment (wound_id, captured_at);
