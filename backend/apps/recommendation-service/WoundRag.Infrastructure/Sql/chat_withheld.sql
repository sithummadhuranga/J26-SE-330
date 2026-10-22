-- The passages the gate withholds for the session's case among the query's best matches in the whole retrieval pool,
-- with the Req(d) fields not observed. Same query and ranking as chat_search.sql, so the count describes the search
-- the model ran. Counted for a notice written by code; never sent to a model.

WITH text_query AS (
    SELECT replace(plainto_tsquery('pg_catalog.english', @query)::text, ' & ', ' | ')::tsquery AS q
),
best AS (
    SELECT g.chunk_id, g.req_prerequisites
    FROM rag.guideline_chunks g CROSS JOIN text_query t
    WHERE g.corpus_version = @version
      AND g.chunk_type = ANY (@types)
      AND g.tsv @@ t.q
    ORDER BY ts_rank_cd(g.tsv, t.q) DESC, g.chunk_id COLLATE "C"
    LIMIT @limit
)
SELECT b.chunk_id AS "ChunkId",
       ARRAY(SELECT f FROM unnest(b.req_prerequisites) AS f WHERE NOT f = ANY (@observed::text[]) ORDER BY f) AS "MissingFields"
FROM best b
WHERE NOT b.req_prerequisites <@ @observed::text[]
ORDER BY b.chunk_id COLLATE "C";
