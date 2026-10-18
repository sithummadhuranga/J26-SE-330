-- The requests the service answered, keyed for idempotency (decision 72). A request key holds the hash of
-- everything Member 4's rebuild never changes; each body under that key is one attempt. The same body is stored
-- once, so a retry replays its answer; a grown healingHistory is a new attempt under the same key; any other
-- difference has no matching key row, so it cannot be stored and the service answers 409.

-- Append-only guard for the Phase B tables. REVOKE would not hold: grants/0001 runs after rag/ on a fresh
-- database and grants UPDATE and DELETE on every rag table.
CREATE FUNCTION rag.reject_change_to_append_only() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
    RAISE EXCEPTION '%.% is append-only: % rejected', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'integrity_constraint_violation';
END $$;

CREATE TABLE rag.request_keys (
    case_id        uuid        NOT NULL,                  -- the assessment id, as Member 4 sends it
    revision       integer     NOT NULL,
    stable_sha256  text        NOT NULL,                  -- the request without healingHistory
    first_seen_at  timestamptz NOT NULL,

    PRIMARY KEY (case_id, revision),
    UNIQUE (case_id, revision, stable_sha256),
    CONSTRAINT revision_positive CHECK (revision >= 1),
    CONSTRAINT stable_sha256_hex CHECK (stable_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE TABLE rag.requests (
    request_id     uuid        PRIMARY KEY,
    case_id        uuid        NOT NULL,
    revision       integer     NOT NULL,
    stable_sha256  text        NOT NULL,
    body_sha256    text        NOT NULL,                  -- the whole request, healingHistory included
    corpus_version text        NOT NULL REFERENCES rag.corpus_versions (corpus_version),
    received_at    timestamptz NOT NULL,

    FOREIGN KEY (case_id, revision, stable_sha256) REFERENCES rag.request_keys (case_id, revision, stable_sha256),
    CONSTRAINT one_attempt_per_body UNIQUE (case_id, revision, body_sha256),
    CONSTRAINT body_sha256_hex CHECK (body_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE TRIGGER request_keys_append_only BEFORE UPDATE OR DELETE ON rag.request_keys
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER request_keys_no_truncate BEFORE TRUNCATE ON rag.request_keys
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER requests_append_only BEFORE UPDATE OR DELETE ON rag.requests
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER requests_no_truncate BEFORE TRUNCATE ON rag.requests
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_change_to_append_only();
