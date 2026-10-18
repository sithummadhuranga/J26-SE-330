-- A frozen corpus version as the service reads it, in four results: its settings (nothing if it is not frozen), its
-- derivation thresholds, the words its stored queries know per slot, and its query templates. @version.

SELECT corpus_version AS "Version", retrieval_k AS "RetrievalK", retrieve_prose AS "RetrieveProse",
       retrieve_rationale AS "RetrieveRationale", embedding_model AS "EmbeddingModel",
       embedding_dimensions AS "EmbeddingDimensions", reranker_model AS "RerankerModel", instruct_hash AS "InstructHash",
       manifest_sha256 AS "ManifestSha256", frozen_at AS "FrozenAt"
FROM rag.corpus_versions
WHERE corpus_version = @version AND frozen_at IS NOT NULL;

SELECT threshold_id AS "Id", value AS "Value", unit AS "Unit"
FROM rag.derivation_thresholds WHERE corpus_version = @version;

SELECT s.key AS "Slot", array_agg(DISTINCT s.value ORDER BY s.value) AS "Words"
FROM rag.query_vectors, jsonb_each_text(slot_values) AS s(key, value)
WHERE corpus_version = @version
GROUP BY s.key;

SELECT template_id FROM rag.query_vectors WHERE corpus_version = @version
GROUP BY template_id ORDER BY template_id COLLATE "C";
