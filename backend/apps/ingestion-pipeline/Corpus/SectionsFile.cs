// Reads, proposes and applies sections.yaml: what each heading of the 7 sub-guidelines is, and which are
// printed over two lines (decisions 6, 14). A confirmed file, because no pattern safely tells a heading
// from an ALL-CAPS line.

using System.Globalization;
using System.Text;
using IngestionPipeline.Pdf;
using WoundRag.Shared.Text;

namespace IngestionPipeline.Corpus;

public sealed class SectionsFileDto
{
    public string Status { get; set; } = "proposed";
    public string? ConfirmedBy { get; set; }
    public string? ConfirmedOn { get; set; }
    public string SourcePdfSha256 { get; set; } = "";
    public string Generated { get; set; } = "";
    public List<SectionsSubGuidelineDto> SubGuidelines { get; set; } = [];
}

public sealed class SectionsSubGuidelineDto
{
    public string Name { get; set; } = "";
    public List<WrapDto> Wraps { get; set; } = [];
    public List<HeadingDto> Headings { get; set; } = [];
}

public sealed class WrapDto
{
    public string First { get; set; } = "";
    public string Second { get; set; } = "";
    public int Page { get; set; }
}

public sealed class HeadingDto
{
    public string Heading { get; set; } = "";
    public int Page { get; set; }
    public string Kind { get; set; } = "";
    public string? Reason { get; set; }
}

public static class SectionsFile
{
    private static readonly Dictionary<string, (string Kind, string? Reason)> Proposed = new(StringComparer.Ordinal)
    {
        ["AUTHORS"] = ("exclude", "author list (decision 6)"),
        ["INSTITUTIONS"] = ("exclude", "author list (decision 6)"),
        ["METHODS"] = ("exclude", "methods (decision 6)"),
        ["METHODOLOGY"] = ("exclude", "methods (decision 6)"),
        ["ACKNOWLEDGEMENTS"] = ("exclude", "acknowledgements (decision 6)"),
        ["GLOSSARY"] = ("exclude", "glossary (decision 6)"),
        ["REFERENCES"] = ("exclude", "references (decision 6)"),
        ["CONFLICT OF INTEREST STATEMENT"] = ("exclude", "administrative (decision 14)"),
        ["CONFLICT OF INTEREST STATEMENTS"] = ("exclude", "administrative (decision 14)"),
        ["CONFLICT OF INTERESTS"] = ("exclude", "administrative (decision 14)"),
        ["MANAGEMENT OF CONFLICT OF INTERESTS"] = ("exclude", "administrative (decision 14)"),
        ["GUIDELINE WRITING GROUP CONFLICT OF INTEREST POLICY"] = ("exclude", "administrative (decision 14)"),
        ["GUIDELINE WORKING GROUP CONFLICT OF INTEREST POLICY"] = ("exclude", "administrative (decision 14)"),
        ["AUTHOR CONTRIBUTIONS"] = ("exclude", "administrative (decision 14)"),
        ["CONTRIBUTION OF AUTHORS"] = ("exclude", "administrative (decision 14)"),
        ["EXTERNAL EXPERTS, PATIENT REPRESENTATIVES AND REVIEW PROCESS"] = ("exclude", "administrative (decision 14)"),
        ["ABSTRACT"] = ("exclude", "summary of the recommendations (decision 14)"),
        ["ABBREVIATIONS"] = ("exclude", "abbreviations, like the glossary (decision 14)"),
        ["RESULTS"] = ("exclude", "systematic-review results, part of the method (decision 14)"),
        ["INTRODUCTION"] = ("prose", null),
        ["BACKGROUND"] = ("prose", null),
        ["TARGET POPULATION AND TARGET AUDIENCE"] = ("prose", null),
        ["DEFINITIONS AND TERMINOLOGY AS USED IN THIS DOCUMENT"] = ("prose", null),
        ["RELATED GUIDELINES"] = ("prose", null),
        ["CONSIDERATIONS"] = ("prose", null),
        ["FURTHER CONSIDERATIONS"] = ("prose", null),
        ["FURTHER CONSIDERATIONS / FUTURE RESEARCH / KEY CONTROVERSIES"] = ("prose", null),
        ["AREAS WITH ABSENT OR INCONSISTENT EVIDENCE"] = ("prose", null),
        ["KEY CONTROVERSIES"] = ("prose", null),
        ["POSTSCRIPT"] = ("prose", null),
        ["FURTHER QUESTIONS"] = ("prose", null),
        ["FUTURE RESEARCH"] = ("prose", null),
        ["FUTURE RESEARCH AGENDA"] = ("prose", null),
        ["FUTURE RESEARCH PRIORITIES"] = ("prose", null),
        ["KEY CONSIDERATIONS FOR FUTURE RESEARCH"] = ("prose", null),
        ["CONCLUDING REMARKS"] = ("prose", null),
    };

    private static readonly (string Guideline, string First, string Second)[] ProposedWraps =
    [
        ("IWGDF Classification Guideline", "FURTHER CONSIDERATIONS / FUTURE RESEARCH / KEY", "CONTROVERSIES"),
        ("Intersocietal PAD Guideline", "EXTERNAL EXPERTS, PATIENT REPRESENTATIVES AND REVIEW", "PROCESS"),
        ("IWGDF Wound Healing Guideline", "AUTOLOGOUS PLATELETS – WITH EXCEPTION OF THE AUTOLOGOUS LEUCOCYTE", "AND PLATELET PATCH"),
        ("IWGDF Wound Healing Guideline", "AGENTS THAT STIMULATE RED CELL PRODUCTION OR PROTEIN", "SUPPLEMENTATION"),
    ];

    private const string Header = """
        # sections.yaml — what each section heading of the 7 recommendation-bearing sub-guidelines is.
        #
        # Written by `ingestion-pipeline -- propose-sections` (status: proposed). The author reviews
        # it, then sets status: confirmed, confirmed_by and confirmed_on. From then on the chunker
        # reads ONLY this confirmed file and fails on any heading it does not list.
        #
        # wraps:    a heading printed over two lines; the two lines are one heading.
        # headings: every heading OUTSIDE the LIST OF RECOMMENDATIONS and body RECOMMENDATIONS
        #           sections (those two are handled by the recommendation chunker), in order:
        #   prose    its text becomes PROSE chunks (decisions 6, 14);
        #   exclude  its text is not chunked; reason says which decision excludes it;
        #   sub      a sub-heading: its text is treated like the section above it.
        # Lines before a sub-guideline's first heading (title page) are not chunked (decision 14).
        # No guideline body text belongs in this file — headings, pages and reasons only.

        """;

    public static SectionHeadings HeadingsFor(SectionsFileDto file, string subGuideline)
    {
        if (file.Status != "confirmed")
            throw new InvalidOperationException("sections.yaml is not confirmed; the chunker reads only a confirmed file (decision 14)");
        return new SectionHeadings(file.SubGuidelines.SingleOrDefault(g => g.Name == subGuideline)
            ?? throw new InvalidOperationException($"sections.yaml does not list {subGuideline}"));
    }

    public static SectionsFileDto? Read(string path) =>
        File.Exists(path) ? AliasFile.Deserializer().Deserialize<SectionsFileDto>(File.ReadAllText(path)) : null;

    public static void Write(string path, SectionsFileDto dto) =>
        File.WriteAllText(path, Header + AliasFile.Serializer().Serialize(dto), new UTF8Encoding(false));

    public static SectionsFileDto Propose(GuidelineDocument doc, IEnumerable<string> subGuidelines, string pdfSha256, DateOnly today)
    {
        var dto = new SectionsFileDto
        {
            SourcePdfSha256 = pdfSha256,
            Generated = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        foreach (var g in subGuidelines)
        {
            var sg = new SectionsSubGuidelineDto { Name = g };
            foreach (var w in ProposedWraps.Where(w => w.Guideline == g))
                sg.Wraps.Add(new WrapDto { First = w.First, Second = w.Second, Page = FindWrapPage(doc, g, w.First, w.Second) });
            var headings = new SectionHeadings(sg);
            foreach (var h in OutsideHeadings(doc, g, headings))
            {
                var (kind, reason) = Proposed.TryGetValue(h.Heading, out var p) ? p : ("sub", null);
                sg.Headings.Add(new HeadingDto { Heading = h.Heading, Page = h.Page, Kind = kind, Reason = reason });
            }
            dto.SubGuidelines.Add(sg);
        }
        return dto;
    }

    private static int FindWrapPage(GuidelineDocument doc, string g, string first, string second)
    {
        var idxs = doc.Positions(g);
        for (int k = 0; k + 1 < idxs.Count; k++)
            if (WhitespaceText.Strip(doc.Flat[idxs[k]].Line) == first && WhitespaceText.Strip(doc.Flat[idxs[k + 1]].Line) == second)
                return doc.Flat[idxs[k]].Page + 1;
        throw new InvalidOperationException($"{g}: wrapped heading \"{first}\" + \"{second}\" not found");
    }

    public static ((int Lo, int Hi) List, (int Lo, int Hi) Body) Ranges(GuidelineDocument doc, string g)
    {
        var idxs = doc.Positions(g);
        var lst = ListSection(doc, idxs, g);
        var bod = doc.FindSection(idxs, BodyEntries.SectionHeading, Patterns.BodyTerminators, after: lst.Hi)
                  ?? throw new InvalidOperationException($"{g}: no body RECOMMENDATIONS");
        return ((lst.Lo - 1, ListItemLines(doc, g).Hi), (bod.Lo - 1, bod.Hi));
    }

    // The list ends at a "Note:" line: Infection prints one after its last item (pdf_index 108).
    // The structure check keeps the uncut section, so its counts don't change.
    public static (int Lo, int Hi) ListItemLines(GuidelineDocument doc, string g)
    {
        var idxs = doc.Positions(g);
        var lst = ListSection(doc, idxs, g);
        for (int pos = lst.Lo; pos < lst.Hi; pos++)
            if (IsNote(doc.Flat[idxs[pos]].Line)) return (lst.Lo, pos);
        return lst;
    }

    public static bool IsNote(string line) => WhitespaceText.Strip(line).StartsWith("Note:", StringComparison.Ordinal);

    private static (int Lo, int Hi) ListSection(GuidelineDocument doc, List<int> idxs, string g) =>
        doc.FindSection(idxs, "LIST OF RECOMMENDATIONS", Patterns.ListTerminators)
        ?? throw new InvalidOperationException($"{g}: no LIST OF RECOMMENDATIONS");

    public static List<(int Pos, string Heading, int Page)> OutsideHeadings(GuidelineDocument doc, string g, SectionHeadings headings)
    {
        var idxs = doc.Positions(g);
        var (lst, bod) = Ranges(doc, g);
        var result = new List<(int, string, int)>();
        for (int pos = 0; pos < idxs.Count; pos++)
        {
            if ((pos >= lst.Lo && pos < lst.Hi) || (pos >= bod.Lo && pos < bod.Hi)) continue;
            var h = headings.At(doc, idxs, pos);
            if (h is null) continue;
            result.Add((pos, h.Value.Heading, doc.Flat[idxs[pos]].Page + 1));
            pos += h.Value.Lines - 1;
        }
        return result;
    }
}

public sealed class SectionHeadings(SectionsSubGuidelineDto sub)
{
    public (string Heading, int Lines)? At(GuidelineDocument doc, List<int> idxs, int pos)
    {
        var line = WhitespaceText.Strip(doc.Flat[idxs[pos]].Line);
        if (!Patterns.SectionHeading.IsMatch(line)) return null;
        if (pos + 1 < idxs.Count)
        {
            var next = WhitespaceText.Strip(doc.Flat[idxs[pos + 1]].Line);
            if (sub.Wraps.Any(w => w.First == line && w.Second == next)) return (line + " " + next, 2);
        }
        return (line, 1);
    }
}
