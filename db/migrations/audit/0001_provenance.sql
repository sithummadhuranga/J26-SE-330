CREATE SCHEMA IF NOT EXISTS audit;

-- Append-only. Service roles get INSERT only; nobody gets UPDATE or DELETE (§9.4, §12).
CREATE TABLE audit.provenance (
    provenance_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id      uuid NOT NULL,
    stage         text NOT NULL CHECK (stage IN (
                      'GATEWAY_ACCEPTED', 'PERSISTED', 'DEDUPLICATED', 'ORCHESTRATION_STARTED',
                      'RAG_RETURNED', 'RECOMMENDATION_STORED', 'DELIVERED')),
    outcome       text NOT NULL,
    device_id     text,
    kafka_ref     text,          -- topic/partition/offset
    trace_id      text,
    rag_audit_ref text,          -- model and corpus version
    recorded_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_provenance_event ON audit.provenance (event_id);
