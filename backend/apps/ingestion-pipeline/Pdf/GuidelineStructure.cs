// Reads the structure of the IWGDF 2023 PDF: the sub-guideline of each page, the recommendation
// list and body sections, and the items, recommendations and rationales in them. A1 and the chunker share it.

using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using WoundRag.Shared.Text;

namespace IngestionPipeline.Pdf;

/// <summary>One item of a LIST OF RECOMMENDATIONS; Marker is "GRADE", "BPS" or null for a parent stem.</summary>
public sealed record ListItem(string Label, string? Top, int Page, string? Section, string Text, string? Marker);

/// <summary>One "Recommendation N:" block of a body RECOMMENDATIONS section.</summary>
public sealed record BodyRecommendation(string Label, int Page, string? Section, string Statement, string Rationale)
{
    public bool HasRationale => Rationale.Length > 0;
}

public sealed class GuidelineDocument
{
    public const string FrontMatter = "(front matter)";

    public int PageCount => RawLines.Count;

    public IReadOnlyList<List<PdfLine>> RawLines { get; }

    public IReadOnlyList<List<string>> Body { get; }

    public IReadOnlyList<string> Labels { get; }

    /// <summary>The whole document as (0-based page, body line), in reading order.</summary>
    public IReadOnlyList<(int Page, string Line)> Flat { get; }

    private GuidelineDocument(List<List<PdfLine>> raw)
    {
        RawLines = raw;
        Body = raw.Select(r => PdfLines.Lines(r, Band.Body)).ToList();
        Labels = SegmentSubGuidelines(raw);
        Flat = Body.SelectMany((lines, page) => lines.Select(l => (page, l))).ToList();
    }

    /// <summary>The document without lines whose top falls in one of the zones (decision 30).</summary>
    public GuidelineDocument Without(IReadOnlyList<(int Page, double Top, double Bottom)> zones) =>
        new(RawLines.Select((lines, i) => lines
            .Where(l => !zones.Any(z => z.Page == i + 1 && l.Y0 >= z.Top && l.Y0 < z.Bottom)).ToList()).ToList());

    /// <summary>Reads every page. A1 passes superscriptUnits false so its text matches the oracle (decision 29).</summary>
    public static GuidelineDocument Load(PdfDocument pdf, bool superscriptUnits = true) =>
        new(pdf.GetPages().Select(p => PdfLines.BuildRawLines(p, superscriptUnits)).ToList());

    // Labels each page from its running header. Blank pages take the NEXT label, because a title
    // page has no header and belongs to the guideline it introduces. A header of just "2023" is blank.
    private static List<string> SegmentSubGuidelines(List<List<PdfLine>> raw)
    {
        var heads = raw.Select(r =>
        {
            var h = PyText.Strip(string.Join(' ', PdfLines.Lines(r, Band.Header)));
            return h.Length == 0 || h == "2023" ? "" : h;
        }).ToList();
        var labels = new string[heads.Count];
        var next = FrontMatter;
        for (int i = heads.Count - 1; i >= 0; i--)
        {
            if (heads[i].Length > 0) next = heads[i];
            labels[i] = next;
        }
        return labels.ToList();
    }

    public List<string> SubGuidelines() =>
        Labels.Distinct().Where(g => g != FrontMatter).ToList();

    /// <summary>Positions in Flat whose page belongs to the guideline.</summary>
    public List<int> Positions(string guideline) =>
        Enumerable.Range(0, Flat.Count).Where(k => Labels[Flat[k].Page] == guideline).ToList();

    // Returns (lo inclusive, hi exclusive) positions within idxs, or null. Cut at the line, not the
    // page: Wound Healing Recommendation 29 shares a page with FURTHER CONSIDERATIONS.
    public (int Lo, int Hi)? FindSection(List<int> idxs, string heading, IReadOnlySet<string> terminators, int after = 0)
    {
        int? start = null;
        for (int pos = after; pos < idxs.Count; pos++)
            if (PyText.Strip(Flat[idxs[pos]].Line) == heading) { start = pos + 1; break; }
        if (start is null) return null;
        for (int pos = start.Value; pos < idxs.Count; pos++)
            if (terminators.Contains(PyText.Strip(Flat[idxs[pos]].Line))) return (start.Value, pos);
        return (start.Value, idxs.Count);
    }

    // An ALL-CAPS heading closes the current item: PAD and Charcot group their lists under
    // DIAGNOSIS / PROGNOSIS / TREATMENT. The chunker passes its own join (decision 11); A1 joins with a space.
    public List<ListItem> ParseItems(List<int> idxs, int lo, int hi, Func<string, string, string>? join = null)
    {
        join ??= static (text, line) => text + " " + line;
        var items = new List<(string Label, string? Top, int Page, string? Section, string Text)>();
        (string Label, string? Top, int Page, string? Section, string Text)? cur = null;
        string? top = null, section = null;
        for (int pos = lo; pos < hi; pos++)
        {
            var (page, raw) = Flat[idxs[pos]];
            var line = PyText.Strip(raw);
            var m = Patterns.Item.Match(line);
            if (m.Success)
            {
                if (cur is not null) items.Add(cur.Value);
                string label;
                if (m.Groups[1].Success)
                {
                    top = m.Groups[1].Value;
                    label = top + (m.Groups[2].Success ? m.Groups[2].Value : "");
                }
                else
                {
                    label = (top ?? "?") + m.Groups[3].Value;
                }
                cur = (label, top, page + 1, section, PyText.Strip(line[(m.Index + m.Length)..]));
            }
            else if (Patterns.Heading.IsMatch(line))
            {
                if (cur is not null) { items.Add(cur.Value); cur = null; }
                section = line;
            }
            else if (cur is not null)
            {
                cur = cur.Value with { Text = join(cur.Value.Text, line) };
            }
        }
        if (cur is not null) items.Add(cur.Value);

        return items.Select(it =>
        {
            var text = PyText.NormalizeSpaces(it.Text);
            string? marker = Patterns.Grade.IsMatch(text) ? "GRADE" : Patterns.Bps.IsMatch(text) ? "BPS" : null;
            return new ListItem(it.Label, it.Top, it.Page, it.Section, text, marker);
        }).ToList();
    }

    // An ALL-CAPS heading closes the current block: Wound Healing puts one before nearly every recommendation.
    public List<BodyRecommendation> ParseBodyRecommendations(List<int> idxs, int lo, int hi)
    {
        var recs = new List<BodyRecommendation>();
        string? label = null, section = null, curSection = null;
        int curPage = 0;
        var statement = new System.Text.StringBuilder();
        var rationale = new System.Text.StringBuilder();
        bool inRationale = false;

        void Close()
        {
            if (label is null) return;
            recs.Add(new BodyRecommendation(label, curPage, curSection,
                PyText.NormalizeSpaces(statement.ToString()), PyText.NormalizeSpaces(rationale.ToString())));
            label = null;
        }

        for (int pos = lo; pos < hi; pos++)
        {
            var (page, raw) = Flat[idxs[pos]];
            var line = PyText.Strip(raw);
            var m = Patterns.RecHeading.Match(line);
            if (m.Success)
            {
                Close();
                label = m.Groups[1].Value;
                curPage = page + 1;
                curSection = section;
                statement.Clear().Append(PyText.Strip(line[(m.Index + m.Length)..]));
                rationale.Clear();
                inRationale = false;
            }
            else if (Patterns.Heading.IsMatch(line))
            {
                Close();
                section = line;
            }
            else if (label is not null)
            {
                if (Patterns.Rationale.IsMatch(line))
                {
                    inRationale = true;
                    // The oracle cuts a fixed 10 characters, len("Rationale:"), whatever the spacing.
                    rationale.Append(PyText.Strip(line.Length > 10 ? line[10..] : "")).Append(' ');
                }
                else if (inRationale)
                {
                    rationale.Append(line).Append(' ');
                }
                else
                {
                    statement.Append(' ').Append(line);
                }
            }
        }
        Close();
        return recs;
    }

    /// <summary>The statement up to the end of its first grade or BPS marker, or null if it has none.</summary>
    public static string? StatementUptoMarker(string text)
    {
        var ends = new[] { Patterns.Grade.Match(text), Patterns.Bps.Match(text) }
            .Where(m => m.Success).Select(m => m.Index + m.Length).ToList();
        return ends.Count == 0 ? null : PyText.NormalizeSpaces(text[..ends.Min()]);
    }

    public static string StripMarker(string text)
    {
        var output = Patterns.Grade.Replace(text, "");
        output = Patterns.Bps.Replace(output, "");
        return PyText.RStrip(PyText.NormalizeSpaces(output.Replace("()", " ")), " .;,");
    }

    /// <summary>True when a Figure/Table match starts a caption ("Figure 1:") rather than referring to one.</summary>
    public static bool IsCaptionOccurrence(string text, Match match)
    {
        int end = match.Index + match.Length;
        return end < text.Length && text[end] == ':';
    }
}
