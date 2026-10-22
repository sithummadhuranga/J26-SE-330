// Measures the structure of the IWGDF 2023 PDF (plan step A1) so the numbers can be checked against StructureBaseline.
// Image duplicates are found by hashing the raw image stream, so the hashes differ from the oracle's.

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;
using WoundRag.Shared.Text;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Pdf;

public sealed record TokenStats(int P50, int P90, int P95, int Max, int Min, int Mean, int N);

public sealed class PdfStructureCounts
{
    public required string SourceSha256 { get; init; }
    public required int Pages { get; init; }
    public required IReadOnlyList<GuidelineRow> Rows { get; init; }
    public int ListItems => Rows.Sum(r => r.ListItems);
    public int ParentStems => Rows.Sum(r => r.ParentStems);
    public int GradeableUnits => ListItems - ParentStems;
    public int GradePairs => Rows.Sum(r => r.Grade);
    public int BpsTotal => Rows.Sum(r => r.Bps);
    public int BodyHeadings => Rows.Sum(r => r.BodyRecommendations);
    public int RationaleBlocks => Rows.Sum(r => r.WithRationale);
    public required int BpsStatement { get; init; }
    public required int BpsRecommendation { get; init; }
    public required int TopLevelNumbers { get; init; }
    public required int StatementsCompared { get; init; }
    public required int StatementsDiffering { get; init; }
    public required TokenStats StatementTokens { get; init; }
    public required TokenStats StatementTokensNoMarker { get; init; }
    public required TokenStats RationaleTokens { get; init; }
    public required int CaptionedFigures { get; init; }
    public required int CaptionedTables { get; init; }
    public required int EmbeddedImages { get; init; }
    public required int JpegImages { get; init; }
    public required int PngImages { get; init; }
    public required int ImagesOnFigurePages { get; init; }
    public required int ImagesOnTitlePages { get; init; }
    public required int ImagesElsewhere { get; init; }
    public required int DuplicateImages { get; init; }
    public required int FiguresUnanchorable { get; init; }
    public required int FigureRefsInStatements { get; init; }
    public required IReadOnlyList<(string Guideline, string Label, string Reference)> FigureLinks { get; init; }
    public required int ProvenanceStrings { get; init; }
    public required (int Distinct, int Total) FigureXrefs { get; init; }
    public required (int Distinct, int Total) TableXrefs { get; init; }
    public required int FooterPagesWithDigits { get; init; }
    public required IReadOnlyList<(string Label, int Page, int TextLines, int Images)> RasterisedTables { get; init; }
    public required IReadOnlyList<(string Guideline, string Label, string Text)> GradeableStatements { get; init; }
}

public static class PdfStructure
{
    public static PdfStructureCounts Measure(string pdfPath, WordPieceCounter counter)
    {
        string sha;
        using (var fs = File.OpenRead(pdfPath)) sha = Convert.ToHexStringLower(SHA256.HashData(fs));

        using var pdf = PdfDocument.Open(pdfPath);
        var doc = GuidelineDocument.Load(pdf, superscriptUnits: false);
        var guidelines = doc.SubGuidelines();

        var rows = new List<GuidelineRow>();
        var items = new Dictionary<string, List<ListItem>>();
        var recs = new Dictionary<string, List<BodyRecommendation>>();
        int bpsStmt = 0, bpsRecw = 0, compared = 0, diverged = 0, tops = 0;
        foreach (var g in guidelines)
        {
            var pages = Enumerable.Range(0, doc.PageCount).Where(i => doc.Labels[i] == g).ToList();
            var idxs = doc.Positions(g);
            var lst = doc.FindSection(idxs, "LIST OF RECOMMENDATIONS", Patterns.ListTerminators);
            var its = lst is { } l ? doc.ParseItems(idxs, l.Lo, l.Hi) : [];
            // Searched only after the list ends, so "LIST OF RECOMMENDATIONS" is never taken for "RECOMMENDATIONS".
            var bod = doc.FindSection(idxs, "RECOMMENDATIONS", Patterns.BodyTerminators, after: lst?.Hi ?? 0);
            var rs = bod is { } b ? doc.ParseBodyRecommendations(idxs, b.Lo, b.Hi) : [];
            items[g] = its;
            recs[g] = rs;

            bpsStmt += its.Count(i => Patterns.BpsStatement.IsMatch(i.Text));
            bpsRecw += its.Count(i => Patterns.BpsRecommendation.IsMatch(i.Text));
            tops += its.Select(i => i.Top).Distinct().Count();

            // List vs body wording, compared up to the grade marker. A repeated body label keeps
            // the last occurrence, as the oracle's dict did.
            var bodyByLabel = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in rs) bodyByLabel[r.Label] = r.Statement;
            foreach (var it in its)
            {
                if (!bodyByLabel.TryGetValue(it.Label, out var other) || other.Length == 0) continue;
                var a = GuidelineDocument.StatementUptoMarker(it.Text);
                var bb = GuidelineDocument.StatementUptoMarker(other);
                if (a is null || bb is null) continue;
                compared++;
                if (a != bb) diverged++;
            }

            rows.Add(new GuidelineRow(g, pages.Min() + 1, pages.Max() + 1, its.Count,
                its.Count(i => i.Marker == "GRADE"), its.Count(i => i.Marker == "BPS"),
                its.Count(i => i.Marker is null), rs.Count, rs.Count(r => r.HasRationale)));
        }

        var stmts = guidelines.SelectMany(g => items[g].Where(i => i.Marker is not null)
            .Select(i => (g, i.Label, i.Text))).ToList();
        var withMarker = stmts.Select(s => counter.Count(s.Text)).ToList();
        var withoutMarker = stmts.Select(s => counter.Count(GuidelineDocument.StripMarker(s.Text))).ToList();
        var rationaleTokens = guidelines.SelectMany(g => recs[g].Where(r => r.HasRationale)
            .Select(r => counter.Count(r.Rationale))).ToList();

        var captions = new List<(string Kind, string Label, int Page, string Text)>();
        for (int i = 0; i < doc.PageCount; i++)
            foreach (var ln in doc.Body[i])
            {
                var m = Patterns.Caption.Match(WhitespaceText.Strip(ln));
                if (m.Success && WhitespaceText.Strip(m.Groups[2].Value).Length > 0)
                    captions.Add((m.Groups[1].Value.Split(' ')[0], m.Groups[1].Value, i + 1,
                        WhitespaceText.NormalizeSpaces(m.Groups[2].Value)));
            }
        var figs = captions.Where(c => c.Kind == "Figure").ToList();
        var tabs = captions.Where(c => c.Kind == "Table").ToList();

        var images = ReadImages(pdf);
        var figPages = figs.Select(f => f.Page).ToHashSet();
        var titlePages = guidelines.Select(g => Enumerable.Range(0, doc.PageCount).First(i => doc.Labels[i] == g) + 1).ToHashSet();

        var links = new List<(string, string, string)>();
        int inStmt = 0;
        foreach (var g in guidelines)
            foreach (var r in recs[g])
                foreach (var (field, isStatement) in new[] { (r.Statement, true), (r.Rationale, false) })
                    foreach (Match m in Patterns.FigXref.Matches(field))
                    {
                        if (GuidelineDocument.IsCaptionOccurrence(field, m)) continue;
                        links.Add((g, r.Label, WhitespaceText.NormalizeSpaces(m.Groups[1].Value)));
                        if (isStatement) inStmt++;
                    }
        int unanchorable = figs.Count(f =>
            !recs.TryGetValue(doc.Labels[f.Page - 1], out var rl) || rl.Count == 0);

        var bodyTextAll = string.Join("\n", doc.Body.Select(p => string.Join("\n", p)));
        int provenance = Patterns.Provenance.Sum(p => p.Pattern.Matches(bodyTextAll).Count);
        var collapsed = Regex.Replace(bodyTextAll, @"\s+", " ");

        var digits = new Regex(@"\b\d{1,3}\b");
        int footerDigits = doc.RawLines.Count(page =>
            digits.IsMatch(string.Join(' ', page.Where(l => l.Y0 > PdfLines.FooterY).Select(l => l.Text))));

        return new PdfStructureCounts
        {
            SourceSha256 = sha,
            Pages = doc.PageCount,
            Rows = rows,
            BpsStatement = bpsStmt,
            BpsRecommendation = bpsRecw,
            TopLevelNumbers = tops,
            StatementsCompared = compared,
            StatementsDiffering = diverged,
            StatementTokens = Stats(withMarker),
            StatementTokensNoMarker = Stats(withoutMarker),
            RationaleTokens = Stats(rationaleTokens),
            CaptionedFigures = figs.Count,
            CaptionedTables = tabs.Count,
            EmbeddedImages = images.Count,
            JpegImages = images.Count(i => i.IsJpeg),
            PngImages = images.Count(i => !i.IsJpeg),
            ImagesOnFigurePages = images.Count(i => figPages.Contains(i.Page)),
            ImagesOnTitlePages = images.Count(i => titlePages.Contains(i.Page) && !figPages.Contains(i.Page)),
            ImagesElsewhere = images.Count(i => !figPages.Contains(i.Page) && !titlePages.Contains(i.Page)),
            DuplicateImages = images.Count - images.Select(i => i.Sha256).Distinct().Count(),
            FiguresUnanchorable = unanchorable,
            FigureRefsInStatements = inStmt,
            FigureLinks = links.Distinct().Order().ToList(),
            ProvenanceStrings = provenance,
            FigureXrefs = XrefForms(Patterns.FigXref, collapsed),
            TableXrefs = XrefForms(Patterns.TabXref, collapsed),
            FooterPagesWithDigits = footerDigits,
            RasterisedTables = RasterisedTables(pdf, doc, tabs, images),
            GradeableStatements = stmts,
        };
    }

    /// <summary>Nearest-rank percentile: the smallest value with at least pct% of the data at or below it.</summary>
    public static int Percentile(IReadOnlyList<int> values, double pct)
    {
        if (values.Count == 0) return 0;
        var ordered = values.Order().ToList();
        int k = Math.Max(1, Math.Min(ordered.Count, (int)Math.Ceiling(pct / 100.0 * ordered.Count)));
        return ordered[k - 1];
    }

    // The mean is floor-divided, as the oracle did.
    private static TokenStats Stats(IReadOnlyList<int> v) => v.Count == 0
        ? new TokenStats(0, 0, 0, 0, 0, 0, 0)
        : new TokenStats(Percentile(v, 50), Percentile(v, 90), Percentile(v, 95), v.Max(), v.Min(),
            v.Sum() / v.Count, v.Count);

    private static (int Distinct, int Total) XrefForms(Regex pattern, string text)
    {
        var forms = pattern.Matches(text).Select(m => WhitespaceText.NormalizeSpaces(m.Groups[1].Value)).ToList();
        return (forms.Distinct(StringComparer.Ordinal).Count(), forms.Count);
    }

    private sealed record ImageInfo(int Page, bool IsJpeg, string Sha256, double Top, double Bottom);

    // Image XObjects only, each counted once per page, as PyMuPDF's get_images() lists them.
    private static List<ImageInfo> ReadImages(PdfDocument pdf)
    {
        var result = new List<ImageInfo>();
        foreach (var page in pdf.GetPages())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var img in page.GetImages())
            {
                if (img.IsInlineImage) continue;
                var hash = Convert.ToHexStringLower(SHA256.HashData(img.RawMemory.Span));
                if (!seen.Add(hash)) continue;
                result.Add(new ImageInfo(page.Number, IsDct(img.ImageDictionary), hash,
                    page.Height - img.BoundingBox.Top, page.Height - img.BoundingBox.Bottom));
            }
        }
        return result;
    }

    private static bool IsDct(DictionaryToken dict)
    {
        if (!dict.TryGet(NameToken.Filter, out var filter)) return false;
        return filter switch
        {
            NameToken n => n.Data == "DCTDecode",
            ArrayToken a => a.Data.OfType<NameToken>().Any(n => n.Data == "DCTDecode"),
            _ => false,
        };
    }

    // A captioned table whose band holds an image and fewer than 3 text lines is printed as a picture.
    private static List<(string, int, int, int)> RasterisedTables(PdfDocument pdf, GuidelineDocument doc,
        List<(string Kind, string Label, int Page, string Text)> tabs, List<ImageInfo> images)
    {
        var found = new List<(string, int, int, int)>();
        foreach (var pg in tabs.Select(t => t.Page).Distinct().Order())
        {
            var bodyRows = doc.RawLines[pg - 1]
                .Where(l => l.Y0 >= PdfLines.HeaderY && l.Y0 <= PdfLines.FooterY && WhitespaceText.Strip(l.Text).Length > 0)
                .Select(l => (Y: l.Y0, Text: WhitespaceText.Strip(l.Text)))
                .OrderBy(r => r.Y).ThenBy(r => r.Text, StringComparer.Ordinal).ToList();
            var capsHere = bodyRows
                .Select(r => (r.Y, M: Patterns.Caption.Match(r.Text), r.Text))
                .Where(x => x.M.Success && x.Text.StartsWith("Table", StringComparison.Ordinal))
                .Select(x => (x.Y, Label: x.M.Groups[1].Value)).ToList();
            for (int k = 0; k < capsHere.Count; k++)
            {
                double capY = capsHere[k].Y;
                double next = k + 1 < capsHere.Count ? capsHere[k + 1].Y : PdfLines.FooterY;
                int nText = bodyRows.Count(r => capY + 5 < r.Y && r.Y < next);
                int nImg = images.Count(i => i.Page == pg && i.Bottom > capY && i.Top < next);
                if (nImg > 0 && nText < 3) found.Add((capsHere[k].Label, pg, nText, nImg));
            }
        }
        return found;
    }
}
