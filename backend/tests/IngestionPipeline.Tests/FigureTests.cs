// Plan step A5 (decision 22): the inventory reconciles with the structure check, a JPEG export is the PDF's
// stream byte for byte, a PNG holds exactly its decoded samples and ICC profile, and links come only from
// cross-references. The real-PDF tests skip without the PDF and never print guideline text.

using IngestionPipeline;
using IngestionPipeline.Corpus;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Tests;

public class FigureTests
{
    [Fact]
    public void Png_round_trip_keeps_rgb_samples_and_the_icc_profile_exactly()
    {
        var samples = Enumerable.Range(0, 5 * 3 * 3).Select(i => (byte)(i * 7)).ToArray();
        var icc = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var r = Png.Read(Png.Write(5, 3, samples, 3, null, icc));
        Assert.Equal((5, 3, 2), (r.Width, r.Height, r.ColourType));
        Assert.Equal(samples, r.Samples);
        Assert.Equal(icc, r.Icc);
        Assert.Null(r.Palette);
    }

    [Fact]
    public void Png_round_trip_keeps_palette_indices_and_the_palette()
    {
        var samples = new byte[] { 0, 1, 2, 1, 0, 2 };
        var palette = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 };
        var r = Png.Read(Png.Write(3, 2, samples, 1, palette, [1, 2, 3]));
        Assert.Equal(3, r.ColourType);
        Assert.Equal(samples, r.Samples);
        Assert.Equal(palette, r.Palette);
    }

    private static (PdfDocument Pdf, GuidelineDocument Doc, List<CaptionedImage> Found)? Load()
    {
        var paths = RepoPaths.Discover();
        if (!File.Exists(paths.SourcePdf)) return null;
        var pdf = PdfDocument.Open(paths.SourcePdf);
        var doc = GuidelineDocument.Load(pdf);
        var tables = TablesFile.Read(Path.Combine(paths.Patterns, "tables.yaml"))!;
        var found = Figures.Inventory(pdf, doc, tables.Tables.Where(t => t.Treatment == "transcribe").Select(t => (t.SubGuideline, t.Label)));
        return (pdf, doc, found);
    }

    [CorpusFact]
    public void Fourteen_figures_and_the_two_PAD_table_images_are_found()
    {
        var l = Load();
        if (l is null) throw new InvalidOperationException("source PDF is missing");
        using var pdf = l.Value.Pdf;
        var found = l.Value.Found;
        Assert.Equal(14, found.Count(f => f.Label.StartsWith("Figure")));   // A1: 14 captioned figures
        Assert.Equal([("Table 1D", 172), ("Table 1E", 172)],
            found.Where(f => f.Label.StartsWith("Table")).Select(f => (f.Label, f.PdfIndex)));
        Assert.Equal(8, found.Count(f => f.SubGuideline == "IWGDF Practical Guidelines"));
    }

    [CorpusFact]
    public void Every_export_is_faithful_to_the_pdf()
    {
        var l = Load();
        if (l is null) throw new InvalidOperationException("source PDF is missing");
        using var pdf = l.Value.Pdf;
        int jpeg = 0, png = 0;
        foreach (var f in l.Value.Found)
        {
            var ex = Figures.Export(pdf, f.Image);
            if (ex.MediaType == "image/jpeg")
            {
                Assert.Equal(f.Image.RawMemory.ToArray(), ex.Bytes);
                Assert.Equal(ex.SourceStreamSha256, ex.Sha256);
                jpeg++;
            }
            else
            {
                var r = Png.Read(ex.Bytes);
                Assert.Equal((f.Image.WidthInSamples, f.Image.HeightInSamples), (r.Width, r.Height));
                Assert.Equal(ex.Samples, r.Samples);
                Assert.Equal(f.Label.StartsWith("Table") ? "Generic RGB Profile" : "sRGB IEC61966-2.1", IccDescription(r.Icc));
                png++;
            }
        }
        Assert.Equal((9, 7), (jpeg, png));   // 9 figures JPEG; 5 figures + 2 table images Flate
    }

    [CorpusFact]
    public void Links_come_only_from_rationale_cross_references_never_from_captions()
    {
        var paths = RepoPaths.Discover();
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        var doc = GuidelineDocument.Load(pdf);
        var aliases = AliasFile.Read(Path.Combine(paths.Patterns, "label_aliases.yaml"))!;
        var sections = SectionsFile.Read(Path.Combine(paths.Patterns, "sections.yaml"))!;
        var counter = WordPieceCounter.Load(paths.Vocabulary);
        var x = Figures.Xrefs(RecommendationChunks.Build(doc, aliases, sections, counter), RationaleChunks.Build(doc, aliases, sections, counter));
// 0 references in statements, 4 in rationales (CHA 10, PAD 13, 18, 20); PAD 17 shares PAD 18's rationale.
        Assert.DoesNotContain(x, r => r.SourceField == "statement");
        Assert.Equal(["CHA-R10", "PAD-R13", "PAD-R17", "PAD-R18", "PAD-R20"], x.Select(r => r.RecommendationChunkId).Order(StringComparer.Ordinal));
        Assert.All(x, r => Assert.DoesNotContain(r.FigureLabel + ":", r.Evidence));
    }

    private static string IccDescription(byte[] p)
    {
        int Be(int at) => (p[at] << 24) | (p[at + 1] << 16) | (p[at + 2] << 8) | p[at + 3];
        for (int t = 0, n = Be(128); t < n; t++)
        {
            var o = 132 + 12 * t;
            if (System.Text.Encoding.ASCII.GetString(p, o, 4) != "desc") continue;
            var off = Be(o + 4);
            return System.Text.Encoding.ASCII.GetString(p, off + 12, Be(off + 8) - 1);
        }
        return "";
    }
}
