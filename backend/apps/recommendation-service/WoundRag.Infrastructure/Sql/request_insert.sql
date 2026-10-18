-- One attempt under a key. Nothing is returned when this body is already stored (a racing copy of the request).
-- @requestId, @caseId, @revision, @stable, @body, @version, @receivedAt.

INSERT INTO rag.requests (request_id, case_id, revision, stable_sha256, body_sha256, corpus_version, received_at)
VALUES (@requestId, @caseId, @revision, @stable, @body, @version, @receivedAt)
ON CONFLICT ON CONSTRAINT one_attempt_per_body DO NOTHING
RETURNING request_id;
