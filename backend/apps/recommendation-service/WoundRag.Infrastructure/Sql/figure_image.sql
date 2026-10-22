-- One figure's stored bytes, only from a frozen version. @version, @figureId.

SELECT f.bytes AS "Bytes", f.media_type AS "MediaType", f.figure_sha256 AS "Sha256"
FROM rag.figures f
JOIN rag.corpus_versions v ON v.corpus_version = f.corpus_version
WHERE f.corpus_version = @version AND f.figure_id = @figureId AND v.frozen_at IS NOT NULL;
