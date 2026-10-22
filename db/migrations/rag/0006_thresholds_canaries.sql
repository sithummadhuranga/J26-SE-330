-- Numeric clinical thresholds with the guideline sentence they come from, and fixed canary texts
-- whose stored embeddings show if the embedding model behind the same name ever changes.

CREATE TABLE rag.derivation_thresholds (
    corpus_version  text        NOT NULL REFERENCES rag.corpus_versions (corpus_version),
    threshold_id    text        NOT NULL,      -- e.g. 'bps15.par_percent'
    value           numeric     NOT NULL,
    unit            text        NOT NULL,
    quoted_source   text        NOT NULL,
    pdf_index       integer     NOT NULL,
    chunk_id        text,

    PRIMARY KEY (corpus_version, threshold_id),
    CONSTRAINT quoted_source_not_blank CHECK (length(btrim(quoted_source)) > 0),
    CONSTRAINT pdf_index_positive CHECK (pdf_index >= 1),
    FOREIGN KEY (corpus_version, chunk_id) REFERENCES rag.guideline_chunks (corpus_version, chunk_id)
);

CREATE TABLE rag.embedding_canaries (
    corpus_version   text            NOT NULL REFERENCES rag.corpus_versions (corpus_version),
    canary_id        text            NOT NULL,
    text             text            NOT NULL,
    embedding        rag.vector(256) NOT NULL,
    embedding_model  text            NOT NULL,
    embedded_at      timestamptz     NOT NULL,

    PRIMARY KEY (corpus_version, canary_id)
);

CREATE TRIGGER derivation_thresholds_frozen BEFORE INSERT OR UPDATE OR DELETE ON rag.derivation_thresholds
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_frozen_version();
CREATE TRIGGER derivation_thresholds_no_truncate BEFORE TRUNCATE ON rag.derivation_thresholds
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_truncate_with_frozen_version();
CREATE TRIGGER embedding_canaries_frozen BEFORE INSERT OR UPDATE OR DELETE ON rag.embedding_canaries
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_frozen_version();
CREATE TRIGGER embedding_canaries_no_truncate BEFORE TRUNCATE ON rag.embedding_canaries
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_truncate_with_frozen_version();
