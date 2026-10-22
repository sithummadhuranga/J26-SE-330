-- The closed query set and its rerank scores, both computed offline. Queries are built from fixed
-- slot values only, so every query that can run is known in advance and no patient text reaches a model.

CREATE TABLE rag.query_vectors (
    corpus_version   text        NOT NULL REFERENCES rag.corpus_versions (corpus_version),
    -- SHA-256(query_text || '|' || retrieval_filter): the same text in another subspace is another key.
    query_key        text        NOT NULL,
    query_text       text        NOT NULL,
    retrieval_filter text        NOT NULL,
    template_id      text        NOT NULL,
    slot_values      jsonb       NOT NULL,
    embedding        rag.vector(256) NOT NULL,

    PRIMARY KEY (corpus_version, query_key),
    CONSTRAINT query_key_is_sha256_hex CHECK (query_key ~ '^[0-9a-f]{64}$'),
    CONSTRAINT query_key_matches_text_and_filter CHECK (
        query_key = encode(sha256(convert_to(query_text || '|' || retrieval_filter, 'UTF8')), 'hex')),
    CONSTRAINT slot_values_is_object CHECK (jsonb_typeof(slot_values) = 'object')
);

CREATE TABLE rag.rerank_scores (
    corpus_version  text             NOT NULL,
    query_key       text             NOT NULL,
    chunk_id        text             NOT NULL,
    chunk_hash      text             NOT NULL,   -- the chunk as scored; A9 checks it is still current
    rrf_rank        integer          NOT NULL,
    score           double precision NOT NULL,
    reranker_model  text             NOT NULL,   -- as returned by the API
    instruct_hash   text             NOT NULL,
    scored_at       timestamptz      NOT NULL,

    PRIMARY KEY (corpus_version, query_key, chunk_id),
    UNIQUE (corpus_version, query_key, rrf_rank),
    CONSTRAINT rrf_rank_positive CHECK (rrf_rank >= 1),
    CONSTRAINT chunk_hash_is_sha256_hex CHECK (chunk_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT instruct_hash_is_sha256_hex CHECK (instruct_hash ~ '^[0-9a-f]{64}$'),
    FOREIGN KEY (corpus_version, query_key) REFERENCES rag.query_vectors (corpus_version, query_key),
    FOREIGN KEY (corpus_version, chunk_id)  REFERENCES rag.guideline_chunks (corpus_version, chunk_id)
);

CREATE TRIGGER query_vectors_frozen BEFORE INSERT OR UPDATE OR DELETE ON rag.query_vectors
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_frozen_version();
CREATE TRIGGER query_vectors_no_truncate BEFORE TRUNCATE ON rag.query_vectors
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_truncate_with_frozen_version();
CREATE TRIGGER rerank_scores_frozen BEFORE INSERT OR UPDATE OR DELETE ON rag.rerank_scores
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_frozen_version();
CREATE TRIGGER rerank_scores_no_truncate BEFORE TRUNCATE ON rag.rerank_scores
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_truncate_with_frozen_version();
