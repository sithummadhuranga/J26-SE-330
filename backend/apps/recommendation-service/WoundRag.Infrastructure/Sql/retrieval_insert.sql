-- One retrieved candidate with its gate verdict. Run once per row.

INSERT INTO rag.retrievals (request_id, corpus_version, query_key, chunk_id, chunk_hash, rrf_rank, rerank_score, admissible, missing_fields)
VALUES (@requestId, @version, @QueryKey, @ChunkId, @ChunkHash, @RrfRank, @RerankScore, @Admissible, @MissingFields);
