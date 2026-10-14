// `annotate` (plan step A4): applies annotation.yaml to the RECOMMENDATION chunks and prints the variant A, C
// and union counts. The console prints counts and chunk IDs only; clause text goes to the gitignored .cache/.

using System.Text;
using System.Text.Json;
using IngestionPipeline.Corpus;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Steps;

public static class AnnotateCommand
{
    private static string AnnotationPath(RepoPaths p) => Path.Combine(p.Patterns, "annotation.yaml");

    public static int Annotate(RepoPaths paths)
    {
        var file = AnnotationFile.Read(AnnotationPath(paths)) ?? throw new FileNotFoundException(AnnotationPath(paths));
        var aliases = AliasFile.Read(Path.Combine(paths.Patterns, "label_aliases.yaml"))!;
        var sections = SectionsFile.Read(Path.Combine(paths.Patterns, "sections.yaml"))!;
        var counter = WordPieceCounter.Load(paths.Vocabulary);
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        var doc = TableChunks.RunningText(pdf, GuidelineDocument.Load(pdf), paths.Corpus);
        var recs = RecommendationChunks.Build(doc, aliases, sections, counter);

        var unknown = file.AuthorPrerequisites.Keys.Except(recs.Select(r => r.ChunkId)).ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException($"annotation.yaml: author_prerequisites names no such chunk: {string.Join(", ", unknown)}");

        var annotator = new Annotator(file);
        var notes = recs.Select(annotator.Annotate).ToList();
        var thresholds = Annotator.LocateThresholds(file, recs);

        Console.WriteLine($"annotation.yaml status: {file.Status}; primary variant: {file.PrimaryVariant ?? "NOT DECIDED"}");
        Console.WriteLine($"RECOMMENDATION chunks: {notes.Count}");
        Console.WriteLine($"  with a variant-A clause {notes.Count(n => n.ClauseA is not null)}; " +
                          $"with at least one variant-C clause {notes.Count(n => n.ClausesC.Count > 0)}; " +
                          $"with neither {notes.Count(n => n.ClauseA is null && n.ClausesC.Count == 0)}");
        Console.WriteLine();
        var reqU = notes.Select(n => Annotator.Required(n, "union")).ToList();
        Console.WriteLine($"{"Req(d) distribution",-28} {"variant A",10} {"variant C",10} {"union+auth",10}");
        Console.WriteLine($"{"empty Req(d)",-28} {notes.Count(n => n.ReqA.Count == 0),10} {notes.Count(n => n.ReqC.Count == 0),10} {reqU.Count(r => r.Count == 0),10}");
        foreach (var f in Annotator.Vocabulary.Order(StringComparer.Ordinal))
            Console.WriteLine($"{"requires " + f,-28} {notes.Count(n => n.ReqA.Contains(f)),10} {notes.Count(n => n.ReqC.Contains(f)),10} {reqU.Count(r => r.Contains(f)),10}");
        Console.WriteLine($"{"can block (non-empty)",-28} {notes.Count(n => n.ReqA.Count > 0),10} {notes.Count(n => n.ReqC.Count > 0),10} {reqU.Count(r => r.Count > 0),10}");
        Console.WriteLine();
        Console.WriteLine($"polarity: RECOMMEND {notes.Count(n => n.Polarity == "RECOMMEND")}, PROHIBIT {notes.Count(n => n.Polarity == "PROHIBIT")}");
        foreach (var g in notes.SelectMany(n => n.Flags.Select(f => (f, n.ChunkId))).GroupBy(x => x.f).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"  flag '{g.Key}': {g.Count()}  {string.Join(" ", g.Select(x => x.ChunkId))}");
        Console.WriteLine();
        foreach (var t in thresholds)
            Console.WriteLine($"threshold {t.ThresholdId}: {t.Value} {t.Unit}, {t.ChunkId}, pdf_index {t.PdfIndex}, quote {t.QuotedSource.Split(' ').Length} words");

        WriteJsonl(paths, "annotations.jsonl", notes);
        WriteJsonl(paths, "derivation_thresholds.jsonl", thresholds);
        WriteReview(paths, recs, notes);
        Console.WriteLine($"wrote {Path.GetRelativePath(paths.Root, paths.Cache)}/{{annotations,derivation_thresholds}}.jsonl and annotation_review.md (gitignored: contain guideline text)");
        return 0;
    }

    private static void WriteReview(RepoPaths paths, List<RecommendationChunk> recs, List<ChunkAnnotation> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Annotation review (generated — do not edit; gitignored, contains guideline text)");
        sb.AppendLine();
        sb.AppendLine("Each statement is shown without its GRADE marker. `⟦A …⟧` is the variant-A clause, `⟦C …⟧` each");
        sb.AppendLine("variant-C clause. Check against the PDF: does each clause stop where the population ends, and");
        sb.AppendLine("does each listed field really gate this recommendation? Req(d) is regex-derived and");
        sb.AppendLine("clinician-unvalidated.");
        sb.AppendLine();
        var byId = recs.ToDictionary(r => r.ChunkId);
        foreach (var n in notes.OrderBy(n => n.Flags.Count == 0).ThenBy(n => recs.FindIndex(r => r.ChunkId == n.ChunkId)))
        {
            var s = Annotator.BareStatement(byId[n.ChunkId].Text);
            sb.AppendLine($"## {n.ChunkId} (pdf_index {byId[n.ChunkId].PdfIndex}) — {n.Polarity}");
            if (n.Flags.Count > 0) sb.AppendLine($"**Flags:** {string.Join("; ", n.Flags)}");
            sb.AppendLine();
            sb.AppendLine("A: " + Mark(s, n.ClauseA is null ? [] : [n.ClauseA]));
            sb.AppendLine();
            sb.AppendLine("C: " + Mark(s, n.ClausesC));
            sb.AppendLine();
            sb.AppendLine($"- Req(d) A: {List(n.ReqA)} · mentioned A: {List(n.MentionedA)}");
            sb.AppendLine($"- Req(d) C: {List(n.ReqC)} · mentioned C: {List(n.MentionedC)}");
            if (n.ReqAuthor.Count > 0) sb.AppendLine($"- Req(d) set by the author: {List(n.ReqAuthor)}");
            sb.AppendLine($"- clause ends: A {n.ClauseA?.EndedBy ?? "—"}; C {(n.ClausesC.Count == 0 ? "—" : string.Join(", ", n.ClausesC.Select(c => c.EndedBy)))}");
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(paths.Cache, "annotation_review.md"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string List(List<string> xs) => xs.Count == 0 ? "∅" : string.Join(", ", xs);

    private static string Mark(string s, List<Clause> clauses)
    {
        var sb = new StringBuilder();
        var at = 0;
        foreach (var c in clauses.OrderBy(c => c.Start))
        {
            sb.Append(s, at, c.Start - at).Append('⟦').Append(s, c.Start, c.End - c.Start).Append('⟧');
            at = c.End;
        }
        return sb.Append(s, at, s.Length - at).ToString();
    }

    private static void WriteJsonl<T>(RepoPaths paths, string name, IEnumerable<T> rows)
    {
        using var w = new StreamWriter(Path.Combine(paths.Cache, name), false, new UTF8Encoding(false));
        foreach (var row in rows) w.WriteLine(JsonSerializer.Serialize(row));
    }
}
