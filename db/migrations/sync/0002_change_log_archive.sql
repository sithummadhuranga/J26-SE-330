-- §9.4: a change-log row is "kept until every device cursor has passed it, then eligible for archival".
-- Housekeeping moves such rows here (same columns, plus when they were moved), so the table pull reads stays small.
-- Pull never reads the archive: every device that could still need these rows already has them.
CREATE TABLE sync.change_log_archive (
    server_seq    bigint PRIMARY KEY,
    facility_id   text NOT NULL,
    device_id     text,
    change_type   text NOT NULL,
    assessment_id uuid NOT NULL,
    revision      integer NOT NULL,
    created_at    timestamptz NOT NULL,
    archived_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_change_log_archive_assessment ON sync.change_log_archive (assessment_id, revision);
