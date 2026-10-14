-- One row per corpus version, with what its manifest records. Once frozen_at is set nothing of
-- that version may change (a correction is a new version); the guard functions below enforce it.

CREATE TABLE rag.corpus_versions (
    corpus_version        text        PRIMARY KEY,   -- e.g. 'iwgdf-2023.r1'
    source_pdf_sha256     text        NOT NULL,
    chunker_version       text        NOT NULL,
    embedding_model       text        NOT NULL,      -- as returned by the API
    embedding_dimensions  integer     NOT NULL,
    reranker_model        text,                      -- set by A8
    instruct_hash         text,                      -- set by A8
    retrieval_k           integer     NOT NULL,
    postgres_version      text        NOT NULL,
    pgvector_version      text        NOT NULL,
    table_serializer      text        NOT NULL,      -- decision 3
    retrieve_prose        boolean     NOT NULL,      -- decision 6
    retrieve_rationale    boolean     NOT NULL,      -- decision 6
    alias_file_sha256     text        NOT NULL,      -- the confirmed label_aliases.yaml (decision 2)
    manifest_sha256       text,                      -- set at freeze (A9)
    created_at            timestamptz NOT NULL DEFAULT now(),
    frozen_at             timestamptz,

    CONSTRAINT corpus_version_format CHECK (corpus_version ~ '^[a-z0-9][a-z0-9.-]*$'),
    CONSTRAINT source_pdf_sha256_hex CHECK (source_pdf_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT embedding_dimensions_256 CHECK (embedding_dimensions = 256),
    CONSTRAINT retrieval_k_positive CHECK (retrieval_k >= 1),
    CONSTRAINT table_serializer_named CHECK (table_serializer ~ '^table-v[0-9]+$'),
    CONSTRAINT alias_file_sha256_hex CHECK (alias_file_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT hashes_hex CHECK (
        (instruct_hash IS NULL OR instruct_hash ~ '^[0-9a-f]{64}$')
        AND (manifest_sha256 IS NULL OR manifest_sha256 ~ '^[0-9a-f]{64}$')),
    CONSTRAINT frozen_requires_manifest CHECK (
        frozen_at IS NULL
        OR (manifest_sha256 IS NOT NULL AND reranker_model IS NOT NULL AND instruct_hash IS NOT NULL))
);

-- Row guard for every corpus table: no insert, update or delete touching a frozen version.
CREATE FUNCTION rag.reject_change_to_frozen_version() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
    IF TG_OP IN ('UPDATE', 'DELETE') AND EXISTS (
        SELECT 1 FROM rag.corpus_versions v
        WHERE v.corpus_version = OLD.corpus_version AND v.frozen_at IS NOT NULL) THEN
        RAISE EXCEPTION 'corpus version % is frozen: % on %.% rejected',
            OLD.corpus_version, TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF TG_OP IN ('INSERT', 'UPDATE') AND EXISTS (
        SELECT 1 FROM rag.corpus_versions v
        WHERE v.corpus_version = NEW.corpus_version AND v.frozen_at IS NOT NULL) THEN
        RAISE EXCEPTION 'corpus version % is frozen: % on %.% rejected',
            NEW.corpus_version, TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END $$;

-- TRUNCATE skips row triggers, so it is refused while any version is frozen.
CREATE FUNCTION rag.reject_truncate_with_frozen_version() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM rag.corpus_versions WHERE frozen_at IS NOT NULL) THEN
        RAISE EXCEPTION 'TRUNCATE on %.% rejected: a frozen corpus version exists', TG_TABLE_SCHEMA, TG_TABLE_NAME
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NULL;
END $$;

-- Setting frozen_at the first time is the freeze itself; after that the row is fixed.
CREATE FUNCTION rag.reject_change_to_frozen_version_row() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
    IF OLD.frozen_at IS NOT NULL THEN
        RAISE EXCEPTION 'corpus version % is frozen: % rejected', OLD.corpus_version, TG_OP
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER corpus_versions_frozen BEFORE UPDATE OR DELETE ON rag.corpus_versions
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_frozen_version_row();
CREATE TRIGGER corpus_versions_no_truncate BEFORE TRUNCATE ON rag.corpus_versions
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_truncate_with_frozen_version();
