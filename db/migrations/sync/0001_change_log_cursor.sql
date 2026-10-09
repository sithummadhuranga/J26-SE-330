-- The sync schema itself was created by _bootstrap/0000.
CREATE TABLE sync.change_log (
    server_seq    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    facility_id   text NOT NULL,
    device_id     text,
    change_type   text NOT NULL CHECK (change_type IN (
                      'PERSISTED', 'RECOMMENDATION_READY', 'ADVICE_DEFERRED', 'SUPERSEDED')),
    assessment_id uuid NOT NULL,
    revision      integer NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);

-- Supports the "re-send anything from the last 60 seconds" rule in §7.2.
CREATE INDEX ix_change_log_facility_seq ON sync.change_log (facility_id, server_seq);
CREATE INDEX ix_change_log_created ON sync.change_log (created_at);

CREATE TABLE sync.device_cursor (
    device_id  text PRIMARY KEY,
    last_seq   bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now()
);
