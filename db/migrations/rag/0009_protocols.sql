-- The answer sent for a request, byte for byte, so a retry replays exactly what was sent, and the citations it
-- carried. A citation keeps its chunk hash, so it still names the text it cited (and a later chat can start from it).

CREATE TABLE rag.protocols (
    request_id      uuid        PRIMARY KEY REFERENCES rag.requests (request_id),
    corpus_version  text        NOT NULL REFERENCES rag.corpus_versions (corpus_version),
    mode            text        NOT NULL,
    retrieval_mode  text        NOT NULL,
    response_body   bytea       NOT NULL,
    response_sha256 text        NOT NULL,
    created_at      timestamptz NOT NULL,

    CONSTRAINT mode_known CHECK (mode IN ('generated', 'extractive')),                 -- contract 1.0 `mode`
    CONSTRAINT retrieval_mode_known CHECK (retrieval_mode IN ('hybrid', 'lexical')),   -- contract 1.0 `retrievalMode`
    CONSTRAINT response_sha256_matches CHECK (response_sha256 = encode(sha256(response_body), 'hex'))
);

CREATE TABLE rag.protocol_citations (
    request_id     uuid NOT NULL REFERENCES rag.protocols (request_id),
    tag            text NOT NULL,
    corpus_version text NOT NULL,
    chunk_id       text NOT NULL,
    chunk_hash     text NOT NULL,

    PRIMARY KEY (request_id, tag),
    FOREIGN KEY (corpus_version, chunk_id) REFERENCES rag.guideline_chunks (corpus_version, chunk_id),
    CONSTRAINT tag_format CHECK (tag ~ '^S[0-9]+$'),                                   -- contract 1.0 citation tag
    CONSTRAINT chunk_hash_hex CHECK (chunk_hash ~ '^[0-9a-f]{64}$')
);

CREATE TRIGGER protocols_append_only BEFORE UPDATE OR DELETE ON rag.protocols
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER protocols_no_truncate BEFORE TRUNCATE ON rag.protocols
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER protocol_citations_append_only BEFORE UPDATE OR DELETE ON rag.protocol_citations
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER protocol_citations_no_truncate BEFORE TRUNCATE ON rag.protocol_citations
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_change_to_append_only();
