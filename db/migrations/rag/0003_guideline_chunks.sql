-- The corpus: one row per chunk of the IWGDF 2023 guidelines, with its embedding, lexical index,
-- gate prerequisites Req(d) and chunk_hash. Each corpus decision is a CHECK, so a bad row can't load.

CREATE TABLE rag.guideline_chunks (
    corpus_version     text        NOT NULL REFERENCES rag.corpus_versions (corpus_version),
    chunk_id           text        NOT NULL,
    chunk_type         text        NOT NULL,
    sub_guideline      text        NOT NULL,
    section            text,
    recommendation_id  text,                   -- the printed label, e.g. '7a'
    parent_chunk_id    text,                   -- TABLE_ROW -> its TABLE
    -- Many recommendations can share one rationale (Charcot runs several under one).
    rationale_chunk_id text,
    context_header     text        NOT NULL,
    text               text        NOT NULL,   -- context header + body, NFC; what is embedded and hashed
    variant_text       text,                   -- the non-canonical (list) wording; never hashed or cited
    source             text        NOT NULL,
    -- The PDF prints no page numbers, so the 1-based page index is the only locator.
    pdf_index          integer     NOT NULL,
    pdf_index_end      integer,
    chunk_hash         text        NOT NULL,   -- SHA-256 over NFC(corpus_version || chunk_id || text)
    token_count        integer     NOT NULL,
    embedding          rag.vector(256),        -- NULL only for TABLE parents
    -- The two-argument form with a fixed config: a generated column needs an immutable expression.
    tsv                tsvector    GENERATED ALWAYS AS (to_tsvector('pg_catalog.english', text)) STORED,
    req_prerequisites  text[]      NOT NULL DEFAULT '{}',   -- Req(d): fields named in the population clause
    mentioned_fields   text[]      NOT NULL DEFAULT '{}',   -- named elsewhere; never gates
    polarity           text,
    grade_strength     text,
    grade_certainty    text,
    is_best_practice   boolean     NOT NULL DEFAULT false,
    body_label         text,                   -- the body block's label ('8' for split sub-item '8b')
    list_label         text,
    alias_labels       text[]      NOT NULL DEFAULT '{}',
    text_divergence    boolean,                -- list wording differs from the body wording
    split_connector    text,                   -- e.g. 'or' between split sub-items (decision 10)

    PRIMARY KEY (corpus_version, chunk_id),
    UNIQUE (corpus_version, chunk_hash),

    CONSTRAINT chunk_type_known CHECK (chunk_type IN
        ('RECOMMENDATION', 'RATIONALE', 'PROSE', 'TABLE', 'TABLE_ROW')),
    CONSTRAINT chunk_hash_is_sha256_hex CHECK (chunk_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT text_starts_with_context_header CHECK (starts_with(text, context_header)),
    CONSTRAINT pdf_index_positive CHECK (pdf_index >= 1 AND (pdf_index_end IS NULL OR pdf_index_end >= pdf_index)),
    CONSTRAINT token_count_non_negative CHECK (token_count >= 0),
    CONSTRAINT embedding_iff_not_table CHECK ((chunk_type = 'TABLE') = (embedding IS NULL)),
    CONSTRAINT recommendation_fields_only_on_recommendations CHECK (
        chunk_type = 'RECOMMENDATION'
        OR (recommendation_id IS NULL AND polarity IS NULL AND grade_strength IS NULL
            AND grade_certainty IS NULL AND NOT is_best_practice
            AND rationale_chunk_id IS NULL AND variant_text IS NULL
            AND cardinality(req_prerequisites) = 0)),
    CONSTRAINT parent_only_on_table_rows CHECK ((chunk_type = 'TABLE_ROW') = (parent_chunk_id IS NOT NULL)),
    CONSTRAINT polarity_known CHECK (polarity IN ('RECOMMEND', 'PROHIBIT')),
    CONSTRAINT grade_strength_known CHECK (grade_strength IN ('Strong', 'Conditional', 'Weak')),
    CONSTRAINT grade_certainty_known CHECK (grade_certainty IN ('Very low', 'Low', 'Moderate', 'High')),
    CONSTRAINT recommendation_has_label_and_polarity CHECK (
        chunk_type <> 'RECOMMENDATION' OR (recommendation_id IS NOT NULL AND polarity IS NOT NULL)),
    CONSTRAINT recommendation_is_graded_xor_bps CHECK (
        chunk_type <> 'RECOMMENDATION'
        OR (is_best_practice AND grade_strength IS NULL AND grade_certainty IS NULL)
        OR (NOT is_best_practice AND grade_strength IS NOT NULL AND grade_certainty IS NOT NULL)),
    -- Decisions 1 and 4: where the canonical text came from.
    CONSTRAINT source_known CHECK (source IN ('body', 'list', 'transcribed_from_image')),
    CONSTRAINT list_source_only_for_recommendations CHECK (source <> 'list' OR chunk_type = 'RECOMMENDATION'),
    CONSTRAINT transcription_only_for_tables CHECK (
        source <> 'transcribed_from_image' OR chunk_type IN ('TABLE', 'TABLE_ROW')),
    CONSTRAINT label_fields_only_on_recommendations CHECK (
        chunk_type = 'RECOMMENDATION'
        OR (body_label IS NULL AND list_label IS NULL AND cardinality(alias_labels) = 0
            AND text_divergence IS NULL AND split_connector IS NULL)),
    CONSTRAINT recommendation_labels_complete CHECK (
        chunk_type <> 'RECOMMENDATION'
        OR (list_label IS NOT NULL AND text_divergence IS NOT NULL
            AND (source = 'body') = (body_label IS NOT NULL))),
    -- Decision 10: the id is the body label, or the body label plus one sub-letter.
    CONSTRAINT recommendation_id_extends_body_label CHECK (
        body_label IS NULL
        OR (starts_with(recommendation_id, body_label)
            AND (recommendation_id = body_label OR recommendation_id = body_label || right(recommendation_id, 1))
            AND recommendation_id ~ '^[0-9]{1,2}[a-h]?$')),
    CONSTRAINT connector_only_on_split_items CHECK (
        split_connector IS NULL OR (body_label IS NOT NULL AND recommendation_id <> body_label)),
    CONSTRAINT aliases_exclude_own_label CHECK (NOT (recommendation_id = ANY (alias_labels))),
    -- Decision 2: {CODE}-R{number padded to two digits}{sub-letter}, e.g. OFF-R07a.
    CONSTRAINT recommendation_chunk_id_format CHECK (
        chunk_type <> 'RECOMMENDATION'
        OR (chunk_id ~ '^[A-Z]{3}-R[0-9]{2}[a-h]?$'
            AND substr(chunk_id, 6) = lpad(substring(recommendation_id FROM '^[0-9]+'), 2, '0')
                                      || coalesce(substring(recommendation_id FROM '[a-h]$'), ''))),
    -- Decision 7: the five clinical fields plus the derived trajectory. woundBedLabels never gates.
    CONSTRAINT req_prerequisites_vocabulary CHECK (req_prerequisites <@ ARRAY[
        'ulcerLocation', 'protectiveSensation', 'pedalPulses', 'probeToBone', 'infectionGrade',
        'healingTrajectory']::text[]),
    CONSTRAINT recommendation_id_unique UNIQUE (corpus_version, sub_guideline, recommendation_id),
    CONSTRAINT list_label_unique UNIQUE (corpus_version, sub_guideline, list_label),

    FOREIGN KEY (corpus_version, parent_chunk_id)    REFERENCES rag.guideline_chunks (corpus_version, chunk_id),
    FOREIGN KEY (corpus_version, rationale_chunk_id) REFERENCES rag.guideline_chunks (corpus_version, chunk_id)
);

CREATE INDEX guideline_chunks_tsv_gin ON rag.guideline_chunks USING gin (tsv);
CREATE INDEX guideline_chunks_req_gin ON rag.guideline_chunks USING gin (req_prerequisites);
-- No vector index on purpose: rerank scores are stored for the exact top-K, and an approximate
-- index could return a different top-K at request time.

CREATE TRIGGER guideline_chunks_frozen BEFORE INSERT OR UPDATE OR DELETE ON rag.guideline_chunks
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_frozen_version();
CREATE TRIGGER guideline_chunks_no_truncate BEFORE TRUNCATE ON rag.guideline_chunks
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_truncate_with_frozen_version();
