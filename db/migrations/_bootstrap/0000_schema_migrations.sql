-- Migration ledger (architecture §9.2). Created by the migrator before any other migration runs.
CREATE SCHEMA IF NOT EXISTS sync;

CREATE TABLE IF NOT EXISTS sync.schema_migrations (
    schema_name text        NOT NULL,
    version     integer     NOT NULL,
    description text        NOT NULL,
    checksum    text        NOT NULL,  -- SHA-256 of the file contents
    applied_at  timestamptz NOT NULL DEFAULT now(),
    applied_by  text        NOT NULL DEFAULT current_user,
    PRIMARY KEY (schema_name, version)
);
