-- A request key's stable hash and, when this exact body was answered, the body that was sent.
-- @caseId, @revision, @body (the body's SHA-256).

SELECT k.stable_sha256 AS "StableSha256", p.response_body AS "ResponseForBody"
FROM rag.request_keys k
LEFT JOIN rag.requests r ON r.case_id = k.case_id AND r.revision = k.revision AND r.body_sha256 = @body
LEFT JOIN rag.protocols p ON p.request_id = r.request_id
WHERE k.case_id = @caseId AND k.revision = @revision;
