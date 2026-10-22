// Reads a body RECOMMENDATIONS section into entries with page ranges, statements and own rationales.
// Separate from the structure parser, which must stay as the oracle's: the chunker also needs page ranges,
// decision-11 joins, the confirmed headings, and the four Offloading "No recommendation" entries.

using System.Text.RegularExpressions;
using IngestionPipeline.Pdf;
using WoundRag.Shared.Text;

namespace IngestionPipeline.Corpus;

public enum BodyEntryKind
{
    Recommendation,
    NoRecommendation,
    SectionText,
}

public sealed record BodyRationale(int FirstPage, int LastPage, string Text, IReadOnlyList<(int Offset, int Page)> LineStarts)
{
    public int PageAt(int offset) => LineStarts.Last(s => s.Offset <= offset).Page;
}

public sealed record BodyEntry(BodyEntryKind Kind, string? Label, string Section, int SectionIndex,
    int FirstPage, int LastStatementPage, string Statement, BodyRationale? Rationale, BodyRationale? Text = null);

public static partial class BodyEntries
{
    public const string SectionHeading = "RECOMMENDATIONS";

    [GeneratedRegex(@"^Recommendation\s*:")]
    // "Recommendation:" with no label: only the four Offloading "No recommendation" entries.
    private static partial Regex UnnumberedHeading();

    public static List<BodyEntry> Read(GuidelineDocument doc, string subGuideline, SectionHeadings headings)
    {
        var idxs = doc.Positions(subGuideline);
        var lst = doc.FindSection(idxs, "LIST OF RECOMMENDATIONS", Patterns.ListTerminators);
        if (lst is null) return [];
        var bod = doc.FindSection(idxs, SectionHeading, Patterns.BodyTerminators, after: lst.Value.Hi);
        if (bod is null) return [];

        var entries = new List<BodyEntry>();
        string section = SectionHeading;
        int sectionIndex = 0;

        BodyEntryKind kind = default;
        string? label = null;
        bool open = false, inRationale = false;
        int firstPage = 0, lastStatementPage = 0, ratFirst = 0, ratLast = 0;
        string entrySection = section, statement = "", rationale = "";
        int entrySectionIndex = 0;
        var ratStarts = new List<(int Offset, int Page)>();
        string loose = "", looseSection = section;
        int looseFirst = 0, looseLast = 0, looseSectionIndex = 0;
        var looseStarts = new List<(int Offset, int Page)>();

        void Close()
        {
            if (!open) return;
            entries.Add(new BodyEntry(kind, label, entrySection, entrySectionIndex, firstPage, lastStatementPage,
                WhitespaceText.NormalizeSpaces(statement),
                inRationale ? new BodyRationale(ratFirst, ratLast, rationale, ratStarts.ToList()) : null));
            open = false;
        }

        void Open(BodyEntryKind k, string? l, int page, string rest)
        {
            Close();
            (kind, label, open, inRationale) = (k, l, true, false);
            (firstPage, lastStatementPage) = (page, page);
            (entrySection, entrySectionIndex) = (section, sectionIndex);
            (statement, rationale) = (rest, "");
        }

        for (int pos = bod.Value.Lo; pos < bod.Value.Hi; pos++)
        {
            var (page0, raw) = doc.Flat[idxs[pos]];
            int page = page0 + 1;
            var line = WhitespaceText.Strip(raw);
            var numbered = Patterns.RecHeading.Match(line);
            var unnumbered = UnnumberedHeading().Match(line);
            var heading = numbered.Success || unnumbered.Success ? null : headings.At(doc, idxs, pos);

            if (numbered.Success || unnumbered.Success || heading is not null) CloseLoose();
            if (numbered.Success)
                Open(BodyEntryKind.Recommendation, numbered.Groups[1].Value, page, WhitespaceText.Strip(line[(numbered.Index + numbered.Length)..]));
            else if (unnumbered.Success)
                Open(BodyEntryKind.NoRecommendation, null, page, WhitespaceText.Strip(line[(unnumbered.Index + unnumbered.Length)..]));
            else if (heading is not null)
            {
                Close();
                section = heading.Value.Heading;
                sectionIndex++;
                pos += heading.Value.Lines - 1;   // a two-line heading consumes both lines
            }
            else if (!open)
            {
                AppendLoose(line, page);
            }
            else if (Patterns.Rationale.IsMatch(line))
            {
                if (inRationale)
                    throw new InvalidOperationException($"{subGuideline}: a second \"Rationale:\" inside entry {label ?? "(unnumbered)"} on pdf_index {page}");
                inRationale = true;
                (ratFirst, ratLast) = (page, page);
                ratStarts.Clear();
                rationale = "";
                AppendRationale(line[Patterns.Rationale.Match(line).Length..], page);
            }
            else if (inRationale)
            {
                AppendRationale(line, page);
                ratLast = page;
            }
            else
            {
                statement = LineJoin.Join(statement, line);
                lastStatementPage = page;
            }
        }
        Close();
        CloseLoose();
        return entries;

        void AppendLoose(string rawLine, int page)
        {
            var l = WhitespaceText.NormalizeSpaces(rawLine);
            if (l.Length == 0) return;
            if (loose.Length == 0) (looseFirst, looseSection, looseSectionIndex) = (page, section, sectionIndex);
            var joined = LineJoin.Join(loose, l);
            looseStarts.Add((joined.Length - l.Length, page));
            loose = joined;
            looseLast = page;
        }

        void CloseLoose()
        {
            if (loose.Length == 0) return;
            entries.Add(new BodyEntry(BodyEntryKind.SectionText, null, looseSection, looseSectionIndex, looseFirst, looseFirst, "", null,
                new BodyRationale(looseFirst, looseLast, loose, looseStarts.ToList())));
            (loose, looseStarts) = ("", []);
        }

        void AppendRationale(string rawLine, int page)
        {
            var l = WhitespaceText.NormalizeSpaces(rawLine);
            if (l.Length == 0) return;
            var joined = LineJoin.Join(rationale, l);
            ratStarts.Add((joined.Length - l.Length, page));
            rationale = joined;
        }
    }
}
