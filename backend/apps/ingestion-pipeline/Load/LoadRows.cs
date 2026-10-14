// Plan step A7: turns one build of the corpus, the confirmed pattern files and the embedding cache into rag rows.
// Pure: no database, no API. Every mismatch between the inputs fails here, before a transaction is opened.

using System.Text.Json.Nodes;
using IngestionPipeline.Corpus;
using IngestionPipeline.ModelApi;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;

namespace IngestionPipeline.Load;

public sealed record ChunkRow(
    string ChunkId, string ChunkType, string SubGuideline, string? Section, string? RecommendationId,
    string? ParentChunkId, string? RationaleChunkId, string ContextHeader, string Text, string? VariantText,
    string Source, int PdfIndex, int? PdfIndexEnd, string ChunkHash, int TokenCount, float[]? Embedding,
    string[] ReqPrerequisites, string[] MentionedFields, string? Polarity, string? GradeStrength,
    string? GradeCertainty, bool IsBestPractice, string? BodyLabel, string? ListLabel, string[] AliasLabels,
    bool? TextDivergence, string? SplitConnector);

public sealed record QueryRow(string Key, string Text, string Filter, string TemplateId, string SlotValuesJson, float[] Embedding);

public sealed record CanaryRow(string CanaryId, string Text, float[] Embedding, DateTimeOffset EmbeddedAt);

public sealed record FigureRow(string FigureId, string Label, string SubGuideline, int PdfIndex, string Caption, byte[] Bytes, string MediaType, string Sha256);

public sealed record FigureLinkRow(string FigureId, string ChunkId, string SourceField, string Evidence);

public sealed record VerifiedTableRow(
    string Label, string SubGuideline, int PdfIndex, string Status, string? TableChunkId, string Verifier,
    DateTimeOffset VerifiedAt, string? Note, string? SourceImageSha256, string? Pass1By, DateOnly? Pass1On,
    string? Pass1Sha256, string? Pass2WaivedReason);

public static class LoadRows
{
    public static List<ChunkRow> Chunks(BuiltCorpus corpus, IReadOnlyDictionary<string, ChunkAnnotation> notes,
        string variant, IReadOnlyDictionary<string, StoredEmbedding> vectors)
    {
        float[] Vector(string chunkId, string hash) => vectors.TryGetValue(hash, out var e) ? e.Vector
            : throw new InvalidDataException($"{chunkId}: no embedding for its current text; run `embed`");

        var rows = new List<ChunkRow>();
        foreach (var c in corpus.Recommendations)
        {
            var n = notes.TryGetValue(c.ChunkId, out var a) ? a : throw new InvalidDataException($"{c.ChunkId}: not annotated");
// Decision 1: the body wording is canonical; the list wording is kept beside it.
            rows.Add(new ChunkRow(c.ChunkId, "RECOMMENDATION", c.SubGuideline, c.Section, c.RecommendationId,
                null, c.RationaleChunkId, c.ContextHeader, c.Text, c.VariantText, "body", c.PdfIndex, c.PdfIndexEnd,
                c.ChunkHash, c.TokenCount, Vector(c.ChunkId, c.ChunkHash),
                [.. Annotator.Required(n, variant)], [.. Annotator.Mentioned(n, variant)], n.Polarity,
                c.GradeStrength, c.GradeCertainty, c.IsBestPractice, c.BodyLabel, c.ListLabel, [.. c.AliasLabels],
                c.TextDivergence, c.SplitConnector));
        }
        foreach (var c in corpus.Rationales)
            rows.Add(Plain(c.ChunkId, "RATIONALE", c.SubGuideline, c.Section, null, c.ContextHeader, c.Text, "body",
                c.PdfIndex, c.PdfIndexEnd, c.ChunkHash, c.TokenCount, Vector(c.ChunkId, c.ChunkHash)));
        foreach (var c in corpus.Prose.Chunks)
            rows.Add(Plain(c.ChunkId, "PROSE", c.SubGuideline, c.Section, null, c.ContextHeader, c.Text, "body",
                c.PdfIndex, c.PdfIndexEnd, c.ChunkHash, c.TokenCount, Vector(c.ChunkId, c.ChunkHash)));
        foreach (var c in corpus.Tables)
// A TABLE is a parent only and is not embedded.
            rows.Add(Plain(c.ChunkId, c.ChunkType, c.SubGuideline, c.Section, c.ParentChunkId, c.ContextHeader, c.Text,
                c.Source, c.PdfIndex, c.PdfIndexEnd, c.ChunkHash, c.TokenCount,
                c.ChunkType == "TABLE" ? null : Vector(c.ChunkId, c.ChunkHash)));
        return rows;
    }

    private static ChunkRow Plain(string id, string type, string sub, string? section, string? parent, string header,
        string text, string source, int pdfIndex, int? pdfIndexEnd, string hash, int tokens, float[]? vector) =>
        new(id, type, sub, section, null, parent, null, header, text, null, source, pdfIndex, pdfIndexEnd, hash, tokens,
            vector, [], [], null, null, null, false, null, null, [], null, null);

    public static List<QueryRow> Queries(IReadOnlyList<QuerySpec> queries, IReadOnlyDictionary<string, StoredEmbedding> vectors) =>
    [
        .. queries.Select(q =>
        {
            var e = vectors.TryGetValue(EmbeddingCache.TextKey(q.Text), out var v) ? v
                : throw new InvalidDataException($"{q.TemplateId} '{q.Filter}': no embedding for its text; run `embed`");
            var slots = new JsonObject();
            foreach (var (slot, value) in q.SlotValues) slots[slot] = value;
            return new QueryRow(q.Key, q.Text, q.Filter, q.TemplateId, slots.ToJsonString(), e.Vector);
        }),
    ];

    public static List<CanaryRow> Canaries(IReadOnlyList<CanaryDto> canaries, IReadOnlyDictionary<string, StoredEmbedding> vectors, DateTimeOffset embeddedAt) =>
    [
        .. canaries.Select(c => new CanaryRow(c.Id, c.Text,
            vectors.TryGetValue(EmbeddingCache.TextKey(c.Text), out var e) ? e.Vector
                : throw new InvalidDataException($"canary {c.Id}: no embedding for its text; run `embed`"),
            embeddedAt)),
    ];

    public static (List<FigureRow> Figures, List<FigureLinkRow> Links) Figures(
        PdfDocument pdf, BuiltCorpus corpus, TablesFileDto tables, FiguresFileDto figures)
    {
        var found = Corpus.Figures.Inventory(pdf, corpus.Full,
            tables.Tables.Where(t => t.Treatment == "transcribe").Select(t => (t.SubGuideline, t.Label)));
        var xrefs = Corpus.Figures.Xrefs(corpus.Recommendations, corpus.Rationales);
        if (found.Count != figures.Figures.Count)
            throw new InvalidDataException($"the PDF has {found.Count} figures and table images, figures.yaml lists {figures.Figures.Count}");

        var rows = new List<FigureRow>();
        var links = new List<FigureLinkRow>();
        foreach (var entry in figures.Figures)
        {
            var image = found.SingleOrDefault(f => f.SubGuideline == entry.SubGuideline && f.Label == entry.Label && f.PdfIndex == entry.PdfIndex)
                        ?? throw new InvalidDataException($"{entry.FigureId}: not found in the PDF");
            var export = Corpus.Figures.Export(pdf, image.Image);
            if (export.Sha256 != entry.FigureSha256 || export.SourceStreamSha256 != entry.SourceStreamSha256)
                throw new InvalidDataException($"{entry.FigureId}: the image's hash differs from figures.yaml");
            rows.Add(new FigureRow(entry.FigureId, entry.Label, entry.SubGuideline, entry.PdfIndex, image.Caption,
                export.Bytes, export.MediaType, export.Sha256));

            foreach (var link in entry.Links)
            {
// The evidence is the verbatim sentence that names the figure.
                var x = xrefs.FirstOrDefault(x => x.SubGuideline == entry.SubGuideline && x.FigureLabel == entry.Label
                                                  && x.RecommendationChunkId == link.ChunkId && x.SourceField == link.SourceField)
                        ?? throw new InvalidDataException($"{entry.FigureId} → {link.ChunkId}: no cross-reference in the text");
                links.Add(new FigureLinkRow(entry.FigureId, link.ChunkId, link.SourceField, x.Evidence));
            }
        }
        return (rows, links);
    }

    public const string Pass2WaivedReason = "pass 2 waived by the author; pass 1 accepted alone (decision 20)";

    public static List<VerifiedTableRow> VerifiedTables(TablesFileDto tables, IReadOnlyDictionary<string, string> codes, FiguresFileDto figures)
    {
        var verifiedAt = new DateTimeOffset(DateOnly.Parse(tables.ConfirmedOn!, System.Globalization.CultureInfo.InvariantCulture)
            .ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var rows = new List<VerifiedTableRow>();
        foreach (var t in tables.Tables)
        {
            if (t.Treatment == "not_retrievable")
            {
                rows.Add(new VerifiedTableRow(t.Label, t.SubGuideline, t.Page, "not_retrievable", null, tables.ConfirmedBy!,
                    verifiedAt, t.Reason, null, null, null, null, null));
                continue;
            }
            var code = codes[t.SubGuideline];
            var tableId = $"{code}-T{t.Label["Table ".Length..]}";

            var passes = t.Transcriptions.Select(p => (p, t.SinglePass))
                .Concat(t.Regions.SelectMany(r => r.Transcriptions.Select(p => (p, r.SinglePass)))).ToList();
            if (passes.Any(x => x.p.Pass != 1))
                throw new InvalidDataException($"{tableId}: only pass 1 is recorded under decision 20; found pass {passes.First(x => x.p.Pass != 1).p.Pass}");
            if (passes.Count > 1)
                throw new InvalidDataException($"{tableId}: {passes.Count} transcriptions; expected at most one");
            var pass1 = passes.Count == 1 ? passes[0].p : null;
            var waived = passes.Count == 1 && passes[0].SinglePass ? Pass2WaivedReason : null;
            if (pass1 is not null && waived is null)
                throw new InvalidDataException($"{tableId}: pass 1 without pass 2 needs the decision 20 waiver (single_pass)");

            string status, note;
            string? imageSha = null;
            if (t.Regions.Count == 0)
            {
                var imageId = $"{tableId}-IMG";
                imageSha = figures.Figures.SingleOrDefault(f => f.FigureId == imageId)?.FigureSha256
                           ?? throw new InvalidDataException($"{tableId}: no image {imageId} in figures.yaml");
                (status, note) = ("transcribed_by_hand", t.Reason ?? "transcribed by hand from its image (decision 4)");
            }
            else
                (status, note) = ("verified", t.Reason ?? "read from the PDF's cell boxes and checked by eye against the PDF");

            rows.Add(new VerifiedTableRow(t.Label, t.SubGuideline, t.Page, status, tableId, tables.ConfirmedBy!, verifiedAt, note,
                imageSha, pass1?.By, pass1?.On is { } on ? DateOnly.Parse(on, System.Globalization.CultureInfo.InvariantCulture) : null,
                pass1?.Sha256, waived));
        }
        return rows;
    }
}
