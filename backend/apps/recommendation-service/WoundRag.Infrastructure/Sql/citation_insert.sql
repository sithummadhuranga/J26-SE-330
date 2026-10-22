-- One citation of the body sent. Run once per row.

INSERT INTO rag.protocol_citations (request_id, tag, corpus_version, chunk_id, chunk_hash)
VALUES (@requestId, @Tag, @version, @ChunkId, @ChunkHash);
