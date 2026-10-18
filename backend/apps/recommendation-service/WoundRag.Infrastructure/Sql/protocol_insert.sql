-- The body sent, byte for byte; its hash is computed here so it always matches the stored bytes.

INSERT INTO rag.protocols (request_id, corpus_version, mode, retrieval_mode, response_body, response_sha256, created_at)
VALUES (@requestId, @version, @mode, @retrievalMode, @body, encode(sha256(@body), 'hex'), @at);
