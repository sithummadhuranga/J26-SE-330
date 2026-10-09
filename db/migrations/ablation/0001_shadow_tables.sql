-- Duplicate ablation (architecture §13, plan phase 10): "a run with the constraint and inbox switched off ... showing
-- what duplication looks like without the mechanism".
--
-- Dropping the real unique constraints would corrupt the clinical record, so the ablation writes shadow rows instead.
-- With Ablation__Enabled=true the gateway stops answering DUPLICATE (resends reach Kafka again), the persister writes
-- every message it receives here (no unique constraint), and the orchestrator skips its inbox and writes every
-- recommendation it would store here. The real tables keep their protections; these tables show what a store without
-- them would hold. Evaluation only: off in normal operation.
CREATE SCHEMA IF NOT EXISTS ablation;

CREATE TABLE ablation.wound_assessment (
    row_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id      uuid NOT NULL,           -- deliberately not unique
    assessment_id uuid NOT NULL,
    revision      integer NOT NULL,
    device_id     text NOT NULL,
    kafka_ref     text NOT NULL,
    received_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_ablation_assessment_device ON ablation.wound_assessment (device_id);

CREATE TABLE ablation.recommendation (
    row_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id      uuid NOT NULL,           -- deliberately not unique
    assessment_id uuid NOT NULL,
    revision      integer NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_ablation_recommendation_event ON ablation.recommendation (event_id);
