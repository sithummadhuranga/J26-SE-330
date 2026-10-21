-- The follow-up chat (demo decisions 46-52). A chat starts from an answered request, so each answer also stores
-- what a chat needs and the request body is not kept for: the findings line (fixed phrases), the observed fields
-- the gate reads, and the fields the answer withheld. No identifier is stored; a question is stored only after
-- the patient-detail scrubber has run. Every table is append-only: the history read later is the one shown.

CREATE TABLE rag.chat_contexts (
    request_id       uuid    PRIMARY KEY REFERENCES rag.protocols (request_id),
    findings         text    NOT NULL,
    observed_fields  text[]  NOT NULL,               -- Obs(x), in the Req(d) vocabulary
    withheld_fields  text[]  NOT NULL,

    CONSTRAINT findings_not_blank CHECK (length(btrim(findings)) > 0)
);

-- One session per answered request: starting it twice gives the same session.
CREATE TABLE rag.chat_sessions (
    chat_session_id  uuid        PRIMARY KEY,
    request_id       uuid        NOT NULL UNIQUE REFERENCES rag.chat_contexts (request_id),
    corpus_version   text        NOT NULL REFERENCES rag.corpus_versions (corpus_version),
    created_at       timestamptz NOT NULL
);

-- The passages a session may cite. Tags start as the protocol's S1..Sn (added_in_turn 0) and grow by one per new
-- passage; a tag is never reused or renumbered.
CREATE TABLE rag.chat_evidence (
    chat_session_id  uuid    NOT NULL REFERENCES rag.chat_sessions (chat_session_id),
    tag_number       integer NOT NULL,
    corpus_version   text    NOT NULL,
    chunk_id         text    NOT NULL,
    chunk_hash       text    NOT NULL,
    added_in_turn    integer NOT NULL,                -- 0 = from the protocol

    PRIMARY KEY (chat_session_id, tag_number),
    FOREIGN KEY (corpus_version, chunk_id) REFERENCES rag.guideline_chunks (corpus_version, chunk_id),
    CONSTRAINT one_tag_per_chunk UNIQUE (chat_session_id, chunk_id),
    CONSTRAINT tag_number_positive CHECK (tag_number >= 1),
    CONSTRAINT added_in_turn_non_negative CHECK (added_in_turn >= 0),
    CONSTRAINT chunk_hash_hex CHECK (chunk_hash ~ '^[0-9a-f]{64}$')
);

-- One row per question. The turn number is the key, so two questions sent at once cannot both take it: one 409.
CREATE TABLE rag.chat_turns (
    chat_session_id  uuid        NOT NULL REFERENCES rag.chat_sessions (chat_session_id),
    turn_number      integer     NOT NULL,
    question         text        NOT NULL,           -- after the scrubber
    removed          text[]      NOT NULL,           -- kinds of detail removed
    language         text        NOT NULL,
    outcome          text        NOT NULL,
    english          jsonb       NOT NULL,           -- verified English statements [{text, cite}]
    shown            jsonb       NOT NULL,           -- statements shown [{text, cite}]
    shown_language   text        NOT NULL,
    message          text,                           -- fixed text shown instead of statements
    notices          jsonb       NOT NULL,           -- fixed notices added by code
    answered_by      text,
    problem          text,                           -- why nothing was answered; never answer text
    prompt_hash      text        NOT NULL,
    created_at       timestamptz NOT NULL,

    PRIMARY KEY (chat_session_id, turn_number),
    CONSTRAINT turn_number_positive CHECK (turn_number >= 1),
    CONSTRAINT language_known CHECK (language IN ('en', 'si', 'ta') AND shown_language IN ('en', 'si', 'ta')),
    CONSTRAINT outcome_known CHECK (outcome IN ('answered', 'not_covered', 'out_of_scope', 'blocked', 'unavailable')),
    -- An answered turn shows statements and no fixed message; every other turn the reverse.
    CONSTRAINT answered_shows_statements CHECK ((outcome = 'answered') = (message IS NULL AND jsonb_array_length(shown) > 0)),
    CONSTRAINT json_shapes CHECK (
        jsonb_typeof(english) = 'array' AND jsonb_typeof(shown) = 'array' AND jsonb_typeof(notices) = 'array')
);

CREATE TRIGGER chat_contexts_append_only BEFORE UPDATE OR DELETE ON rag.chat_contexts
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER chat_contexts_no_truncate BEFORE TRUNCATE ON rag.chat_contexts
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER chat_sessions_append_only BEFORE UPDATE OR DELETE ON rag.chat_sessions
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER chat_sessions_no_truncate BEFORE TRUNCATE ON rag.chat_sessions
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER chat_evidence_append_only BEFORE UPDATE OR DELETE ON rag.chat_evidence
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER chat_evidence_no_truncate BEFORE TRUNCATE ON rag.chat_evidence
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER chat_turns_append_only BEFORE UPDATE OR DELETE ON rag.chat_turns
    FOR EACH ROW EXECUTE FUNCTION rag.reject_change_to_append_only();
CREATE TRIGGER chat_turns_no_truncate BEFORE TRUNCATE ON rag.chat_turns
    FOR EACH STATEMENT EXECUTE FUNCTION rag.reject_change_to_append_only();
