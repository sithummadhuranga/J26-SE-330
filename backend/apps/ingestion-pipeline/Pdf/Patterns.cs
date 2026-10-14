// The regular expressions and section terminators that read the structure of the IWGDF 2023 PDF.
// Grade, Bps, Heading, Item and the cross-reference patterns are the structure check's own; the parity counts depend on them.

using System.Text.RegularExpressions;

namespace IngestionPipeline.Pdf;

public static class Patterns
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // "1.", "1a." or a detached "a.": Classification uses "1a.", Prevention "8." then "a.".
    public static readonly Regex Item = new(@"^(?:(\d{1,2})([a-h])?|([a-h]))\.(?:\s|$)");

    // PAD separates strength and certainty with a comma, the others with a semicolon.
    public static readonly Regex Grade = new(
        @"(?:GRADE\s+)?(?:Strength\s+of\s+)?(?:recommendation:\s*)?" +
        @"\b(Strong|Conditional|Weak)\b\s*[;,]\s*" +
        @"(?:Certainty\s+of\s+(?:the\s+)?evidence:\s*)?" +
        @"\b(Very\s+low|Low|Moderate|High)\b", Ci);

    // Grade plus "CoE:", which only Offloading body Recommendation 1a prints.
    public static readonly Regex GradeMarker = new(
        @"(?:GRADE\s+)?(?:Strength\s+of\s+)?(?:recommendation:\s*)?" +
        @"\b(Strong|Conditional|Weak)\b\s*[;,]\s*" +
        @"(?:(?:Certainty\s+of\s+(?:the\s+)?evidence|CoE):\s*)?" +
        @"\b(Very\s+low|Low|Moderate|High)\b", Ci);

    // Infection list item 19 says "Best Practice Recommendation", not "Statement".
    public static readonly Regex Bps = new(@"Best\s+Practice\s+(?:Statement|Recommendation)", Ci);

    public static readonly Regex BpsStatement = new(@"Best\s+Practice\s+Statement", Ci);

    public static readonly Regex BpsRecommendation = new(@"Best\s+Practice\s+Recommendation", Ci);

    public static readonly Regex RecHeading = new(@"^Recommendation\s+(\d{1,2}[a-h]?)\s*:");

    // A literal "Rationale:" at the start of a line is the only rationale delimiter in the PDF.
    public static readonly Regex Rationale = new(@"^Rationale\s*:");

    // PAD's tables are "Table 1A:" to "Table 1E:", so the sub-letter can be upper case.
    public static readonly Regex Caption = new(@"^((?:Figure|Table)\s+\d+[A-Za-z]?)\s*[.:–-]?\s*(.+)$");

    // "Fig." and "Figures N and M" never occur; the structure check keeps them to report that.
    public static readonly Regex FigXref = new(
        @"(\(?\s*(?:see\s+|See\s+|in\s+|shown\s+in\s+|presented\s+in\s+|listed\s+in\s+)?" +
        @"(?:Figure|Figures|Fig\.?|figure|figures)\s*\d+[a-z]?" +
        @"(?:\s*(?:and|,|-|to|&|or)\s*\d+[a-z]?)*\s*\)?)");

    public static readonly Regex TabXref = new(
        @"(\(?\s*(?:see\s+|See\s+|in\s+|shown\s+in\s+|presented\s+in\s+|listed\s+in\s+)?" +
        @"(?:Table|Tables)\s*\d+[a-z]?" +
        @"(?:\s*(?:and|,|-|to|&|or)\s*\d+[a-z]?)*\s*\)?)");

    // Searched in body text only, so the copyright footer can't match.
    public static readonly IReadOnlyList<(string Name, Regex Pattern)> Provenance =
    [
        ("reproduced with permission", new Regex("reproduced with permission", Ci)),
        ("adapted from", new Regex("adapted from", Ci)),
        ("courtesy of", new Regex("courtesy of", Ci)),
        ("with permission", new Regex("with permission", Ci)),
        ("reprinted", new Regex("reprinted", Ci)),
        ("modified from", new Regex("modified from", Ci)),
        ("based on (ref)", new Regex(@"[Bb]ased on \(\d+\)")),
    ];

    public static readonly IReadOnlySet<string> ListTerminators = new HashSet<string>(StringComparer.Ordinal)
    {
        "INTRODUCTION", "BACKGROUND", "METHODS", "METHODOLOGY",
        "EXTERNAL EXPERTS, PATIENT REPRESENTATIVES AND REVIEW",
    };

    public static readonly IReadOnlySet<string> BodyTerminators = new HashSet<string>(StringComparer.Ordinal)
    {
        "CONSIDERATIONS", "FURTHER CONSIDERATIONS",
        "FURTHER CONSIDERATIONS / FUTURE RESEARCH / KEY",
        "AREAS WITH ABSENT OR INCONSISTENT EVIDENCE", "POSTSCRIPT",
        "KEY CONSIDERATIONS FOR FUTURE RESEARCH", "FUTURE RESEARCH",
        "FUTURE RESEARCH AGENDA", "CONCLUDING REMARKS", "GLOSSARY",
        "ACKNOWLEDGEMENTS", "REFERENCES",
    };

    public static readonly Regex Heading = new(@"^[A-Z][A-Z \-/&,'()]{5,70}$");

    // Heading widened for the chunker: numbered prefixes, colons, en dashes, curly apostrophes, digits.
    public static readonly Regex SectionHeading = new(@"^(\d{1,2}\.\s)?[A-Z][A-Z0-9 \-–/&,'’():]{5,90}$");
}
