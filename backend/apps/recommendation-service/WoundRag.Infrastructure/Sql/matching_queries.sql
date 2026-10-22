-- The stored queries built from one case's slot values. A row holds only its own template's slots, so "contained in
-- the case's values" selects, per template and filter, the row made from this case's values. @version, @slots (jsonb).

SELECT template_id AS "TemplateId", query_text AS "Text", retrieval_filter AS "Filter"
FROM rag.query_vectors
WHERE corpus_version = @version AND slot_values <@ @slots::jsonb;
