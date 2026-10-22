-- The stored rerank scores of one query key for the passages asked for. @version, @key, @ids (text[]).

SELECT chunk_id, score
FROM rag.rerank_scores
WHERE corpus_version = @version AND query_key = @key AND chunk_id = ANY (@ids);
