-- Guideline figures stored byte-identical, their links to recommendations, and the record of how
-- each captioned table was verified or transcribed by hand (decisions 4, 20, 22, 30).

CREATE TABLE rag.figures (
    corpus_version  text        NOT NULL REFERENCES rag.corpus_versions (corpus_version),
    figure_id       text        NOT NULL,
    label           text        NOT NULL,      -- as printed; labels repeat across sub-guidelines
    sub_guideline   text        NOT NULL,
    pdf_index       integer     NOT NULL,
    caption         text        NOT NULL,
    bytes           bytea       NOT NULL,      -- exactly as embedded in the PDF
    media_type      text        NOT NULL,
    figure_sha256   text        NOT NULL,

    PRIMARY KEY (corpus_version, figure_id),
    CONSTRAINT pdf_index_positive CHECK (pdf_index >= 1),
    CONSTRAINT figure_sha256_matches_bytes CHECK (figure_sha256 = encode(sha256(bytes), 'hex'))
);

CREATE TABLE rag.figure_links (
    corpus_version  text        NOT NULL,
    figure_id       text        NOT NULL,
    chunk_id        text        NOT NULL,
    source_field    text        NOT NULL,
    -- The sentence holding the explicit cross-reference; a figure's own caption never counts.
    link_evidence   text        NOT NULL,

    PRIMARY KEY (corpus_version, figure_id, chunk_id),
    CONSTRAINT source_field_known CHECK (source_field IN ('statement', 'rationale')),
    CONSTRAINT link_evidence_not_blank CHECK (length(btrim(link_evidence)) > 0),
    FOREIGN KEY (corpus_version, figure_id) REFERENCES rag.figures (corpus_version, figure_id),
    FOREIGN KEY (corpus_version, chunk_id)  REFERENCES rag.guideline_chunks (corpus_version, chunk_id)
);

CREATE TABLE rag.verified_tables (
    corpus_version       text        NOT NULL REFERENCES rag.corpus_versions (corpus_version),
    table_label          text        NOT NULL,
    sub_guideline        text        NOT NULL,
    pdf_index            integer     NOT NULL,
    status               text        NOT NULL,
    table_chunk_id       text,
    verifier             text        NOT NULL,
    verified_at          timestamptz NOT NULL,
    note                 text,
    source_image_sha256  text,             -- the image a hand transcription was read from
    pass1_by             text,
    pass1_on             date,
    pass1_sha256         text,
    pass2_by             text,
    pass2_on             date,
    pass2_sha256         text,
    pass2_waived_reason  text,             -- decision 20

    PRIMARY KEY (corpus_version, sub_guideline, table_label),
    CONSTRAINT pdf_index_positive CHECK (pdf_index >= 1),
    CONSTRAINT status_known CHECK (status IN ('verified', 'transcribed_by_hand', 'not_retrievable')),
    CONSTRAINT chunk_iff_retrievable CHECK ((status = 'not_retrievable') = (table_chunk_id IS NULL)),
    CONSTRAINT not_retrievable_has_note CHECK (status <> 'not_retrievable' OR note IS NOT NULL),
    CONSTRAINT transcription_hashes_hex CHECK (
        (source_image_sha256 IS NULL OR source_image_sha256 ~ '^[0-9a-f]{64}$')
        AND (pass1_sha256 IS NULL OR pass1_sha256 ~ '^[0-9a-f]{64}$')
        AND (pass2_sha256 IS NULL OR pass2_sha256 ~ '^[0-9a-f]{64}$')),
    CONSTRAINT pass1_whole CHECK (
        (pass1_by IS NULL) = (pass1_on IS NULL) AND (pass1_on IS NULL) = (pass1_sha256 IS NULL)),
    CONSTRAINT pass2_whole CHECK (
        (pass2_by IS NULL) = (pass2_on IS NULL) AND (pass2_on IS NULL) = (pass2_sha256 IS NULL)),
    -- After a pass 1 there is exactly one of: a pass 2, or the reason it was waived.
    CONSTRAINT pass2_or_waiver CHECK (
        CASE WHEN pass1_sha256 IS NULL
            THEN pass2_sha256 IS NULL AND pass2_waived_reason IS NULL
            ELSE (pass2_sha256 IS NULL) = (pass2_waived_reason IS NOT NULL)
        END),
    -- An image table needs its image and a pass 1; a table read from the PDF's text has no image.
    CONSTRAINT transcription_fits_status CHECK (
        CASE status
            WHEN 'transcribed_by_hand' THEN source_image_sha256 IS NOT NULL AND pass1_sha256 IS NOT NULL
            WHEN 'verified' THEN source_image_sha256 IS NULL
            ELSE source_image_sha256 IS NULL AND pass1_sha256 IS NULL
        END),
    CONSTRAINT transcription_passes_match CHECK (pass2_sha256 IS NULL OR pass2_sha256 = pass1_sha256),
    FOREIGN KEY (corpus_version, table_chunk_id) REFERENCES rag.guideline_chunks (corpus_version, chunk_id)
);

CREATE TRIGGER figures_frozen BEFORE INSERT OR UPDATE OR DELETE ON rag.figures
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_frozen_version();
CREATE TRIGGER figures_no_truncate BEFORE TRUNCATE ON rag.figures
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_truncate_with_frozen_version();
CREATE TRIGGER figure_links_frozen BEFORE INSERT OR UPDATE OR DELETE ON rag.figure_links
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_frozen_version();
CREATE TRIGGER figure_links_no_truncate BEFORE TRUNCATE ON rag.figure_links
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_truncate_with_frozen_version();
CREATE TRIGGER verified_tables_frozen BEFORE INSERT OR UPDATE OR DELETE ON rag.verified_tables
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_frozen_version();
CREATE TRIGGER verified_tables_no_truncate BEFORE TRUNCATE ON rag.verified_tables
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_truncate_with_frozen_version();
