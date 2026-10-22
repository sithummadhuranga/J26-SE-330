-- The chat's search_guideline tool: the lexical arm of retrieve.sql over the same retrieval pool, with the gate in
-- SQL. Req(d) must be within the session's observed fields, so whatever query the model writes, a passage the answer
-- withheld never comes back (demo decision 49). Lexical only: the dense arm needs a vector for free text, which
-- would be a paid embedding call per question; it is not built.

WITH pool AS (
    SELECT chunk_id, chunk_hash, text, tsv
    FROM rag.guideline_chunks
    WHERE corpus_version = @version
      AND chunk_type = ANY (@types)
      AND req_prerequisites <@ @observed::text[]
),
text_query AS (
    SELECT replace(plainto_tsquery('pg_catalog.english', @query)::text, ' & ', ' | ')::tsquery AS q
)
SELECT p.chunk_id   AS "ChunkId",
       p.chunk_hash AS "ChunkHash",
       p.text       AS "Text"
FROM pool p CROSS JOIN text_query t
WHERE p.tsv @@ t.q
ORDER BY ts_rank_cd(p.tsv, t.q) DESC, p.chunk_id COLLATE "C"
LIMIT @limit;
