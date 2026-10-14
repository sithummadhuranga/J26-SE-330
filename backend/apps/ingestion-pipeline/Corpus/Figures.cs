// Plan step A5: finds each captioned figure's image (and the images of tables printed as pictures, decision 4),
// exports it without changing a pixel (decision 22), and links a figure to a recommendation only where the
// text cross-references it. Unmeasured image formats are refused rather than guessed.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;
using WoundRag.Shared.Text;

namespace IngestionPipeline.Corpus;

public sealed record CaptionedImage(string Label, string SubGuideline, int PdfIndex, double CaptionY, string Caption, IPdfImage Image);

public sealed record ExportedImage(byte[] Bytes, string MediaType, string SourceStreamSha256, string Sha256, byte[]? Samples);

public sealed record FigureXref(string FigureLabel, string SubGuideline, string RecommendationChunkId, string SourceField,
    string TextChunkId, string Evidence);

public static class Figures
{
    public static List<CaptionedImage> Inventory(PdfDocument pdf, GuidelineDocument doc, IEnumerable<(string SubGuideline, string Label)> imageTables)
    {
        var wanted = imageTables.ToHashSet();
        var found = new List<CaptionedImage>();
        for (var i = 0; i < doc.PageCount; i++)
        {
            var caps = new List<(string Label, double Y, string Text)>();
            foreach (var ln in doc.RawLines[i])
            {
// The structure check's caption rule, so the count reconciles with it.
                var m = Patterns.Caption.Match(WhitespaceText.Strip(ln.Text));
                if (!m.Success || WhitespaceText.Strip(m.Groups[2].Value).Length == 0) continue;
                var label = m.Groups[1].Value;
                if (label.StartsWith("Figure", StringComparison.Ordinal) || wanted.Contains((doc.Labels[i], label)))
                    caps.Add((label, ln.Y0, WhitespaceText.NormalizeSpaces(m.Groups[2].Value)));
            }
            if (caps.Count == 0) continue;
            var page = pdf.GetPage(i + 1);
            var images = page.GetImages().Where(im => !im.IsInlineImage)
                .Select(im => (Top: page.Height - im.BoundingBox.Top, Image: im)).ToList();
            caps = [.. caps.OrderBy(c => c.Y)];
            for (var k = 0; k < caps.Count; k++)
            {
                var below = k + 1 < caps.Count ? caps[k + 1].Y : double.MaxValue;
// "Directly below": the image's top lies between this caption and the next.
                var under = images.Where(x => x.Top >= caps[k].Y && x.Top < below).ToList();
                var isTable = caps[k].Label.StartsWith("Table", StringComparison.Ordinal);
                if (isTable && under.Count == 0) continue;   // a text table: not an image table
// A figure has exactly one image. A table takes the nearest: PAD Table 1E has a second image further down,
// its "Key:" legend (open item O-9), which is reported as under no caption, not attached.
                if (!isTable && under.Count != 1)
                    throw new InvalidDataException($"{doc.Labels[i]} {caps[k].Label} (pdf_index {i + 1}): {under.Count} images below the caption, expected 1");
                var nearest = under.MinBy(x => x.Top);
                found.Add(new CaptionedImage(caps[k].Label, doc.Labels[i], i + 1, caps[k].Y, caps[k].Text, nearest.Image));
            }
        }
        return found;
    }

// A DCTDecode stream is a JPEG file and is kept byte for byte. A FlateDecode stream is raw samples, so it is
// written as a PNG with exactly those samples and the PDF's own ICC profile.
    public static ExportedImage Export(PdfDocument pdf, IPdfImage image)
    {
        var d = image.ImageDictionary;
        var raw = image.RawMemory.ToArray();
        var rawSha = Convert.ToHexStringLower(SHA256.HashData(raw));
        var filter = FilterName(d);
        if (filter == "DCTDecode")
            return new ExportedImage(raw, "image/jpeg", rawSha, rawSha, null);
        if (filter != "FlateDecode")
            throw new InvalidDataException($"image filter {filter}: no faithful export is defined for it");
        if (d.ContainsKey(NameToken.DecodeParms) || d.ContainsKey(NameToken.Decode))
            throw new InvalidDataException("FlateDecode image with DecodeParms or Decode: not measured in this PDF, refused");
        if (image.BitsPerComponent != 8)
            throw new InvalidDataException($"{image.BitsPerComponent} bits per component: not measured in this PDF, refused");

        byte[] samples;
        using (var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress))
        using (var ms = new MemoryStream()) { z.CopyTo(ms); samples = ms.ToArray(); }

        d.TryGet(NameToken.ColorSpace, out var csToken);
        var cs = Resolve(pdf, csToken!);
        byte[]? palette = null;
        int channels;
        byte[] profile;
        if (cs is ArrayToken a && Name(a, 0) == "Indexed")
        {
            profile = IccProfile(pdf, Resolve(pdf, a.Data[1]));
            var hival = (int)((NumericToken)Resolve(pdf, a.Data[2])).Int;
            palette = Resolve(pdf, a.Data[3]) switch
            {
                StringToken s => s.GetBytes(),
                HexToken h => [.. h.Bytes],
                StreamToken st => st.Data.ToArray(),
                var other => throw new InvalidDataException($"Indexed lookup of type {other.GetType().Name}: refused"),
            };
            if (palette.Length != (hival + 1) * 3)
                throw new InvalidDataException($"Indexed palette has {palette.Length} bytes, expected {(hival + 1) * 3}");
            channels = 1;
        }
        else
        {
            profile = IccProfile(pdf, cs);
            channels = 3;
        }
        var expected = image.WidthInSamples * image.HeightInSamples * channels;
        if (samples.Length != expected)
            throw new InvalidDataException($"decoded {samples.Length} bytes, expected {expected} for {image.WidthInSamples}x{image.HeightInSamples}x{channels}");

        var png = Png.Write(image.WidthInSamples, image.HeightInSamples, samples, channels, palette, profile);
        return new ExportedImage(png, "image/png", rawSha, Convert.ToHexStringLower(SHA256.HashData(png)), samples);
    }

// Captions ("Figure 1:") are not references. A rationale reference links every recommendation it serves.
    public static List<FigureXref> Xrefs(IReadOnlyList<RecommendationChunk> recs, IReadOnlyList<RationaleChunk> rats)
    {
        var result = new List<FigureXref>();
        foreach (var r in recs)
            foreach (var (label, sentence) in Find(r.Text[(r.ContextHeader.Length + 1)..]))
                result.Add(new FigureXref(label, r.SubGuideline, r.ChunkId, "statement", r.ChunkId, sentence));
        foreach (var t in rats.Where(t => t.OwnerLabel is not null))
        {
            var served = recs.Where(r => r.SubGuideline == t.SubGuideline && r.RationaleOwner == t.OwnerLabel).ToList();
            foreach (var (label, sentence) in Find(t.Text[(t.ContextHeader.Length + 1)..]))
                foreach (var r in served)
                    result.Add(new FigureXref(label, t.SubGuideline, r.ChunkId, "rationale", t.ChunkId, sentence));
        }
        return result;
    }

    private static readonly Regex Target = new(@"(?:Figure|figure)\s*(\d+[a-z]?)");

    private static IEnumerable<(string Label, string Sentence)> Find(string text)
    {
        var sentences = Sentences.Split(WhitespaceText.NormalizeSpaces(text));
        foreach (var (_, s) in sentences)
            foreach (Match m in Patterns.FigXref.Matches(s))
            {
                if (GuidelineDocument.IsCaptionOccurrence(s, m)) continue;
                var t = Target.Match(m.Value);
                if (t.Success) yield return ("Figure " + t.Groups[1].Value, s);
            }
    }

    private static string FilterName(DictionaryToken d) => d.TryGet(NameToken.Filter, out var f) ? f switch
    {
        NameToken n => n.Data,
        ArrayToken { Data.Count: 1 } a when a.Data[0] is NameToken n => n.Data,
        _ => throw new InvalidDataException($"image filter chain {f}: not measured in this PDF, refused"),
    } : "none";

    private static IToken Resolve(PdfDocument pdf, IToken t) =>
        t is IndirectReferenceToken r ? pdf.Structure.GetObject(r.Data).Data : t;

    private static string Name(ArrayToken a, int i) => a.Data[i] is NameToken n ? n.Data : "";

    private static byte[] IccProfile(PdfDocument pdf, IToken cs)
    {
        if (cs is not ArrayToken a || Name(a, 0) != "ICCBased")
            throw new InvalidDataException($"colour space {cs}: only ICCBased (measured for every image) is handled");
        var st = (StreamToken)Resolve(pdf, a.Data[1]);
        var data = st.Data.ToArray();
        if (FilterName(st.StreamDictionary) == "none") return data;
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
    }
}
