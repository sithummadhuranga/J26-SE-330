// `ingestion-pipeline -- check-structure` (plan step A1): prints the parity table and a few statements for eye-checking.
// Exits 0 only when every number matches StructureBaseline exactly.

using IngestionPipeline.Pdf;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Steps;

public static class StructureCheckCommand
{
    public sealed record Check(string Group, string Quantity, string Expected, string Actual)
    {
        public bool Pass => Expected == Actual;
    }

    public static int Run(RepoPaths paths, string[] args)
    {
        if (!File.Exists(paths.SourcePdf))
        {
            Console.Error.WriteLine($"source PDF not found: {paths.SourcePdf}. It is never committed; place it there yourself.");
            return 2;
        }
        var counter = WordPieceCounter.Load(paths.Vocabulary);
        var started = DateTime.UtcNow;
        var r = PdfStructure.Measure(paths.SourcePdf, counter);
        var elapsed = DateTime.UtcNow - started;

        var checks = Compare(r);
        Console.WriteLine($"Structure check — C# (PdfPig) vs Python oracle (PyMuPDF 1.28.2), {elapsed.TotalSeconds:F1} s");
        Console.WriteLine();
        foreach (var group in new[] { "acceptance", "additional" })
        {
            Console.WriteLine(group == "acceptance" ? "PLAN.md §M1 acceptance:" : "Additional oracle checks (structure-recon.md):");
            foreach (var c in checks.Where(c => c.Group == group))
                Console.WriteLine($"  {(c.Pass ? "PASS" : "FAIL"),-4}  {c.Quantity,-58} oracle {c.Expected,-26} C# {c.Actual}");
            Console.WriteLine();
        }

        Console.WriteLine("Figure → recommendation links found (captions excluded):");
        foreach (var (g, label, reference) in r.FigureLinks)
            Console.WriteLine($"  {g} | {label} | {reference}");
        Console.WriteLine();

        Console.WriteLine("First and last three gradeable statements (eye-check):");
        var s = r.GradeableStatements;
        foreach (var (g, label, text) in s.Take(3).Concat(s.Skip(Math.Max(3, s.Count - 3))))
            Console.WriteLine($"  [{g} {label}] {text}");
        Console.WriteLine();
        Console.WriteLine("Not ported: the report's §6 Rows × Cols (table geometry); the chunker's table reader covers it.");

        int failed = checks.Count(c => !c.Pass);
        Console.WriteLine(failed == 0
            ? $"RESULT: PASS — all {checks.Count} checks match the oracle exactly."
            : $"RESULT: FAIL — {failed} of {checks.Count} checks differ from the oracle.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Every oracle-vs-C# comparison, the PLAN.md §M1 acceptance group first.</summary>
    public static List<Check> Compare(PdfStructureCounts r)
    {
        var checks = new List<Check>();
        void Add(string group, string q, object expected, object actual) =>
            checks.Add(new Check(group, q, expected.ToString()!, actual.ToString()!));

        const string A = "acceptance", X = "additional";
        Add(A, "Source PDF SHA-256 (first 16)", StructureBaseline.SourcePdfSha256[..16], r.SourceSha256[..16]);
        Add(A, "Pages", StructureBaseline.Pages, r.Pages);
        Add(A, "Sub-guidelines", StructureBaseline.Rows.Count, r.Rows.Count);
        Add(A, "Sub-guideline names and page ranges",
            string.Join(";", StructureBaseline.Rows.Select(x => $"{x.Name}:{x.FirstPage}-{x.LastPage}")),
            string.Join(";", r.Rows.Select(x => $"{x.Name}:{x.FirstPage}-{x.LastPage}")));
        Add(A, "List items / gradeable units / parent stems",
            $"{StructureBaseline.ListItems}/{StructureBaseline.GradeableUnits}/{StructureBaseline.ParentStems}",
            $"{r.ListItems}/{r.GradeableUnits}/{r.ParentStems}");
        Add(A, "GRADE pairs", StructureBaseline.GradePairs, r.GradePairs);
        Add(A, "Best Practice Statement / Recommendation",
            $"{StructureBaseline.BpsStatement}/{StructureBaseline.BpsRecommendation}", $"{r.BpsStatement}/{r.BpsRecommendation}");
        Add(A, "Body `Recommendation N:` headings", StructureBaseline.BodyHeadings, r.BodyHeadings);
        Add(A, "Rationale blocks", StructureBaseline.RationaleBlocks, r.RationaleBlocks);
        Add(A, "List vs body statements that differ (of compared)",
            $"{StructureBaseline.StatementsDiffering} of {StructureBaseline.StatementsCompared}",
            $"{r.StatementsDiffering} of {r.StatementsCompared}");
        Add(A, "Captioned figures / embedded images",
            $"{StructureBaseline.CaptionedFigures}/{StructureBaseline.EmbeddedImages}", $"{r.CaptionedFigures}/{r.EmbeddedImages}");
        Add(A, "Captioned tables", StructureBaseline.CaptionedTables, r.CaptionedTables);
        Add(A, "Figure → recommendation cross-references", StructureBaseline.FigureRecommendationLinks, r.FigureLinks.Count);
        var st = StructureBaseline.StatementTokens;
        Add(A, "Statement tokens p50/p90/p95/max/min",
            $"{st.P50}/{st.P90}/{st.P95}/{st.Max}/{st.Min}",
            $"{r.StatementTokens.P50}/{r.StatementTokens.P90}/{r.StatementTokens.P95}/{r.StatementTokens.Max}/{r.StatementTokens.Min}");

        foreach (var (exp, act) in StructureBaseline.Rows.Zip(r.Rows))
            Add(X, $"Row {exp.Name}",
                $"{exp.Name}|{exp.Pages}|{exp.ListItems}|{exp.Grade}|{exp.Bps}|{exp.ParentStems}|{exp.BodyRecommendations}|{exp.WithRationale}",
                $"{act.Name}|{act.Pages}|{act.ListItems}|{act.Grade}|{act.Bps}|{act.ParentStems}|{act.BodyRecommendations}|{act.WithRationale}");
        Add(X, "Convention A: top-level numbers", StructureBaseline.TopLevelNumbers, r.TopLevelNumbers);
        Add(X, "Statement tokens mean (incl. marker)", StructureBaseline.StatementTokensMean, r.StatementTokens.Mean);
        var nm = StructureBaseline.StatementTokensNoMarker;
        var am = r.StatementTokensNoMarker;
        Add(X, "Statement tokens excl. marker p50/p90/p95/max/min/mean",
            $"{nm.P50}/{nm.P90}/{nm.P95}/{nm.Max}/{nm.Min}/{nm.Mean}",
            $"{am.P50}/{am.P90}/{am.P95}/{am.Max}/{am.Min}/{am.Mean}");
        var rt = StructureBaseline.RationaleTokens;
        Add(X, "Rationale tokens p50/p90/p95/max",
            $"{rt.P50}/{rt.P90}/{rt.P95}/{rt.Max}",
            $"{r.RationaleTokens.P50}/{r.RationaleTokens.P90}/{r.RationaleTokens.P95}/{r.RationaleTokens.Max}");
        Add(X, "Images JPEG / PNG", $"{StructureBaseline.JpegImages}/{StructureBaseline.PngImages}", $"{r.JpegImages}/{r.PngImages}");
        Add(X, "Images on figure pages / title pages / elsewhere",
            $"{StructureBaseline.ImagesOnFigurePages}/{StructureBaseline.ImagesOnTitlePages}/{StructureBaseline.ImagesElsewhere}",
            $"{r.ImagesOnFigurePages}/{r.ImagesOnTitlePages}/{r.ImagesElsewhere}");
        Add(X, "Duplicate images (raw-stream hash)", 0, r.DuplicateImages);
        Add(X, "Figures in sub-guidelines without recommendations", StructureBaseline.FiguresUnanchorable, r.FiguresUnanchorable);
        Add(X, "Figure references inside statements", StructureBaseline.FigureRefsInStatements, r.FigureRefsInStatements);
        Add(X, "Provenance strings", StructureBaseline.ProvenanceStrings, r.ProvenanceStrings);
        Add(X, "Figure xref forms distinct / total",
            $"{StructureBaseline.FigureXrefDistinct}/{StructureBaseline.FigureXrefTotal}", $"{r.FigureXrefs.Distinct}/{r.FigureXrefs.Total}");
        Add(X, "Table xref forms distinct / total",
            $"{StructureBaseline.TableXrefDistinct}/{StructureBaseline.TableXrefTotal}", $"{r.TableXrefs.Distinct}/{r.TableXrefs.Total}");
        Add(X, "Pages with digits in the footer", StructureBaseline.FooterPagesWithDigits, r.FooterPagesWithDigits);
        Add(X, "Captioned tables published as images", StructureBaseline.RasterisedTables, r.RasterisedTables.Count);
        return checks;
    }
}
