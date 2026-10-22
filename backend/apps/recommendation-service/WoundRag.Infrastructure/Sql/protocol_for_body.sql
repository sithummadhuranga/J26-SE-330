-- The body sent for one exact request body. @caseId, @revision, @body.

SELECT p.response_body
FROM rag.requests r
JOIN rag.protocols p ON p.request_id = r.request_id
WHERE r.case_id = @caseId AND r.revision = @revision AND r.body_sha256 = @body;
