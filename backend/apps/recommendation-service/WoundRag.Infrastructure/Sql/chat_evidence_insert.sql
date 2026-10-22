-- A passage a turn's search added to the session's pack. Run once per row.

INSERT INTO rag.chat_evidence (chat_session_id, tag_number, corpus_version, chunk_id, chunk_hash, added_in_turn)
VALUES (@sessionId, @TagNumber, @version, @ChunkId, @ChunkHash, @turn);
