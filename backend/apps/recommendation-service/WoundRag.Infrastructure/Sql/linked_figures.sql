-- The stored figures that any of the given passages cross-reference, each with those passages in ID order.
-- @version, @ids.

SELECT f.figure_id AS "FigureId", f.label AS "Label", f.sub_guideline AS "SubGuideline", f.pdf_index AS "PdfIndex",
       f.caption AS "Caption", array_agg(l.chunk_id ORDER BY l.chunk_id COLLATE "C") AS "LinkedChunkIds"
FROM rag.figure_links l
JOIN rag.figures f ON f.corpus_version = l.corpus_version AND f.figure_id = l.figure_id
WHERE l.corpus_version = @version AND l.chunk_id = ANY (@ids)
GROUP BY f.figure_id, f.label, f.sub_guideline, f.pdf_index, f.caption
ORDER BY f.figure_id COLLATE "C";
