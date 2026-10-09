CREATE SCHEMA IF NOT EXISTS messaging;

-- Transactional outbox: written in the same transaction as the business rows,
-- published to Kafka by the outbox relay using FOR UPDATE SKIP LOCKED (§4, §9.3).
CREATE TABLE messaging.outbox (
    outbox_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    topic        text NOT NULL,
    msg_key      text NOT NULL,
    payload      jsonb NOT NULL,
    headers      jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at   timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz
);

CREATE INDEX ix_outbox_unpublished ON messaging.outbox (outbox_id) WHERE published_at IS NULL;

-- Processed-message table: consumers skip anything they have already handled.
CREATE TABLE messaging.inbox (
    consumer_name text NOT NULL,
    event_id      uuid NOT NULL,
    processed_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (consumer_name, event_id)
);
