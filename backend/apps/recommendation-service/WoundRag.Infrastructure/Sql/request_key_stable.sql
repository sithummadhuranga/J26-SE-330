-- The stable hash a request key holds. @caseId, @revision.

SELECT stable_sha256 FROM rag.request_keys WHERE case_id = @caseId AND revision = @revision;
