-- The latest answer for a caseId and revision that a chat can start from, and its session if it has one. A
-- revision can hold several answers (a grown healingHistory); the latest is the one the phone received last.

SELECT r.request_id      AS "RequestId",
       r.corpus_version  AS "CorpusVersion",
       s.chat_session_id AS "SessionId"
FROM rag.requests r
JOIN rag.chat_contexts c ON c.request_id = r.request_id
LEFT JOIN rag.chat_sessions s ON s.request_id = r.request_id
WHERE r.case_id = @caseId AND r.revision = @revision
ORDER BY r.received_at DESC, r.request_id
LIMIT 1;
