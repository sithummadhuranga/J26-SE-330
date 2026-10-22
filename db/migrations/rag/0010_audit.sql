-- The append-only audit of each request: every generation attempt with the provider, the requested and the
-- returned model, the outcome and the token usage, and the other events the service records about a request.

CREATE TABLE rag.audit (
    audit_id        bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_id      uuid        NOT NULL REFERENCES rag.requests (request_id),
    occurred_at     timestamptz NOT NULL,
    event_type      text        NOT NULL,                 -- e.g. 'generation_attempt'
    provider        text,
    requested_model text,
    returned_model  text,
    attempt         integer,
    outcome         text,
    input_tokens    integer,
    output_tokens   integer,
    latency_ms      integer,
    details         jsonb       NOT NULL DEFAULT '{}',

    CONSTRAINT event_type_format CHECK (event_type ~ '^[a-z][a-z_]*$'),
    CONSTRAINT attempt_positive CHECK (attempt IS NULL OR attempt >= 1),
    CONSTRAINT counts_non_negative CHECK (
        coalesce(input_tokens, 0) >= 0 AND coalesce(output_tokens, 0) >= 0 AND coalesce(latency_ms, 0) >= 0)
);

CREATE TRIGGER audit_append_only BEFORE UPDATE OR DELETE ON rag.audit
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER audit_no_truncate BEFORE TRUNCATE ON rag.audit
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_change_to_append_only();
