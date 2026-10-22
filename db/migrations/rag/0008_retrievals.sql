-- What one request retrieved and what the gate decided for each passage: the evidence behind every refusal.
-- Each row points at a chunk of the frozen corpus the request was answered from.

CREATE TABLE rag.retrievals (
    request_id     uuid             NOT NULL REFERENCES rag.requests (request_id),
    corpus_version text             NOT NULL,
    query_key      text             NOT NULL,             -- no FK: a missing key runs lexical-only
    chunk_id       text             NOT NULL,
    chunk_hash     text             NOT NULL,
    rrf_rank       integer          NOT NULL,
    rerank_score   double precision,                      -- NULL when the stored score was missing (rrf order)
    admissible     boolean          NOT NULL,
    missing_fields text[]           NOT NULL,             -- Req(d) fields not observed, in gate order

    PRIMARY KEY (request_id, query_key, chunk_id),
    FOREIGN KEY (corpus_version, chunk_id) REFERENCES rag.guideline_chunks (corpus_version, chunk_id),
    CONSTRAINT rrf_rank_positive CHECK (rrf_rank >= 1),
    CONSTRAINT chunk_hash_hex CHECK (chunk_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT admissible_iff_nothing_missing CHECK (admissible = (cardinality(missing_fields) = 0))
);

CREATE TRIGGER retrievals_append_only BEFORE UPDATE OR DELETE ON rag.retrievals
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER retrievals_no_truncate BEFORE TRUNCATE ON rag.retrievals
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_change_to_append_only();
