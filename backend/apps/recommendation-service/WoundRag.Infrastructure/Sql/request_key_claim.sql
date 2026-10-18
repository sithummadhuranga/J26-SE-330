-- Claims a request key. A key that exists is left as it is: its stable hash is read next and compared.
-- @caseId, @revision, @stable, @at.

INSERT INTO rag.request_keys (case_id, revision, stable_sha256, first_seen_at)
VALUES (@caseId, @revision, @stable, @at)
ON CONFLICT (case_id, revision) DO NOTHING;
