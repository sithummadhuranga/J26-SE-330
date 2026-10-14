-- The candidate set for one query key: exact dense search and lexical search over the retrieval pool, fused by
-- Reciprocal Rank Fusion (k = 60), top K (decision 25). The service and the rerank step run this same file,
-- so the candidates scored offline are the ones retrieved online. No gate here: it runs afterwards.
-- Ties break on chunk_id COLLATE "C" so ranks do not depend on how the server was installed.

SELECT EXISTS (
    SELECT 1 FROM rag.query_vectors WHERE corpus_version = @version AND query_key = @key
) AS vector_found;

WITH pool AS (
    SELECT chunk_id, chunk_hash, chunk_type, req_prerequisites, embedding, tsv
    FROM rag.guideline_chunks
    WHERE corpus_version = @version AND chunk_type = ANY (@types)
      AND (@subGuideline::text IS NULL OR sub_guideline = @subGuideline)
),
query_vector AS (
    SELECT embedding FROM rag.query_vectors WHERE corpus_version = @version AND query_key = @key
),
dense AS (
    -- Exact cosine distance over every pool chunk, no index. The operator is schema-qualified because
    -- pgvector lives in rag and rag_svc's search_path does not include it.
    SELECT p.chunk_id,
           row_number() OVER (ORDER BY p.embedding OPERATOR(rag.<=>) v.embedding, p.chunk_id COLLATE "C")::int AS rank
    FROM pool p CROSS JOIN query_vector v
),
text_query AS (
    -- Any term may match: plainto_tsquery joins terms with '&', replaced by '|' (decision 25).
    SELECT replace(plainto_tsquery('pg_catalog.english', @query)::text, ' & ', ' | ')::tsquery AS q
),
lexical AS (
    SELECT p.chunk_id,
           row_number() OVER (ORDER BY ts_rank_cd(p.tsv, t.q) DESC, p.chunk_id COLLATE "C")::int AS rank
    FROM pool p CROSS JOIN text_query t
    WHERE p.tsv @@ t.q
),
fused AS (
    -- numeric, not float, so equal sums are equal and the chunk_id tie-break decides.
    SELECT p.chunk_id, p.chunk_hash, p.chunk_type, p.req_prerequisites,
           d.rank AS dense_rank, l.rank AS lexical_rank,
           coalesce(1.0 / (60 + d.rank), 0) + coalesce(1.0 / (60 + l.rank), 0) AS rrf_score
    FROM pool p
    LEFT JOIN dense d USING (chunk_id)
    LEFT JOIN lexical l USING (chunk_id)
    WHERE d.rank IS NOT NULL OR l.rank IS NOT NULL
)
SELECT chunk_id          AS "ChunkId",
       chunk_hash        AS "ChunkHash",
       chunk_type        AS "ChunkType",
       req_prerequisites AS "ReqPrerequisites",
       dense_rank        AS "DenseRank",
       lexical_rank      AS "LexicalRank",
       row_number() OVER (ORDER BY rrf_score DESC, chunk_id COLLATE "C")::int AS "RrfRank"
FROM fused
ORDER BY "RrfRank"
LIMIT @k;
