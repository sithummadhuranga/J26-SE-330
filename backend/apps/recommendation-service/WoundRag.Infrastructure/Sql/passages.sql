-- What composition needs about each delivered passage: its citation fields, its parent table and its text.
-- @version, @ids.

SELECT chunk_id AS "ChunkId", chunk_hash AS "ChunkHash", chunk_type AS "ChunkType", sub_guideline AS "SubGuideline",
       pdf_index AS "PdfIndex", recommendation_id AS "RecommendationId", polarity AS "Polarity",
       is_best_practice AS "IsBestPractice", grade_strength AS "GradeStrength", grade_certainty AS "GradeCertainty",
       parent_chunk_id AS "ParentChunkId", context_header AS "ContextHeader", text AS "Text"
FROM rag.guideline_chunks
WHERE corpus_version = @version AND chunk_id = ANY (@ids);
