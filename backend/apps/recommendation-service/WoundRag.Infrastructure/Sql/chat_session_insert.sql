-- A new session. Two clients starting the same answer's session at once get one session: the loser re-reads it.

INSERT INTO rag.chat_sessions (chat_session_id, request_id, corpus_version, created_at)
VALUES (@sessionId, @requestId, @version, @createdAt)
ON CONFLICT (request_id) DO NOTHING
RETURNING chat_session_id;
