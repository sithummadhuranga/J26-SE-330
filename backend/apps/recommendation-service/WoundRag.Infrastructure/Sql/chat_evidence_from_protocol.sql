-- A new session's evidence pack: the answer's citations with the same tags (S1..Sn) and hashes, turn 0.

INSERT INTO rag.chat_evidence (chat_session_id, tag_number, corpus_version, chunk_id, chunk_hash, added_in_turn)
SELECT @sessionId, substring(tag FROM 2)::integer, corpus_version, chunk_id, chunk_hash, 0
FROM rag.protocol_citations
WHERE request_id = @requestId;
