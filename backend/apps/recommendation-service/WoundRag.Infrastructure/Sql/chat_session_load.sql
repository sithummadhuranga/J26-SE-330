-- A session with its context (result 1), its evidence pack with what each citation shows, in tag order (result 2),
-- and its turns in order (result 3). The passage text and hash come from the chunk itself.

SELECT s.chat_session_id AS "SessionId",
       s.request_id      AS "RequestId",
       r.case_id         AS "CaseId",
       r.revision        AS "Revision",
       s.corpus_version  AS "CorpusVersion",
       c.findings        AS "Findings",
       c.observed_fields AS "Observed",
       c.withheld_fields AS "Withheld"
FROM rag.chat_sessions s
JOIN rag.requests r ON r.request_id = s.request_id
JOIN rag.chat_contexts c ON c.request_id = s.request_id
WHERE s.chat_session_id = @sessionId;

SELECT 'S' || e.tag_number AS "Tag",
       g.chunk_id          AS "ChunkId",
       g.chunk_hash        AS "ChunkHash",
       e.chunk_hash        AS "PackHash",
       g.sub_guideline     AS "SubGuideline",
       g.pdf_index         AS "PdfIndex",
       g.recommendation_id AS "RecommendationId",
       g.is_best_practice  AS "IsBestPractice",
       g.grade_strength    AS "GradeStrength",
       g.grade_certainty   AS "GradeCertainty",
       g.text              AS "Text"
FROM rag.chat_evidence e
JOIN rag.guideline_chunks g ON g.corpus_version = e.corpus_version AND g.chunk_id = e.chunk_id
WHERE e.chat_session_id = @sessionId
ORDER BY e.tag_number;

SELECT turn_number    AS "Number",
       question       AS "Question",
       removed        AS "Removed",
       language       AS "Language",
       outcome        AS "Outcome",
       english::text  AS "EnglishJson",
       shown::text    AS "ShownJson",
       shown_language AS "ShownLanguage",
       message        AS "Message",
       notices::text  AS "NoticesJson",
       answered_by    AS "AnsweredBy",
       created_at     AS "CreatedAt"
FROM rag.chat_turns
WHERE chat_session_id = @sessionId
ORDER BY turn_number;
