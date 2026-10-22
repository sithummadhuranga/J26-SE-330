// The structure numbers (plan step A1) the C# extractor must reproduce exactly; a mismatch is investigated, never edited away.
// Measured by the earlier Phase A prototype (old repo commit aad7ab4) on the PDF with SHA-256 81d8626c…a3f36.

namespace IngestionPipeline.Pdf;

/// <summary>Per-sub-guideline counts: list items, GRADE, BPS, parent stems, body recommendations, with rationale.</summary>
public sealed record GuidelineRow(string Name, int FirstPage, int LastPage, int ListItems, int Grade,
    int Bps, int ParentStems, int BodyRecommendations, int WithRationale)
{
    public int Pages => LastPage - FirstPage + 1;
}

public static class StructureBaseline
{
    public const string SourcePdfSha256 = "81d8626c0104a655c49842a8b92b27947ab1e4fbd5a976c627676acee18a3f36";

    public const int Pages = 349;

    public static readonly IReadOnlyList<GuidelineRow> Rows =
    [
        new("IWGDF Practical Guidelines", 1, 32, 0, 0, 0, 0, 0, 0),
        new("IWGDF Prevention Guideline", 33, 70, 19, 17, 0, 2, 14, 13),
        new("IWGDF Classification Guideline", 71, 101, 7, 7, 0, 0, 7, 5),
        new("IWGDF Infection Guideline", 102, 146, 30, 21, 9, 0, 28, 17),
        new("Intersocietal PAD Guideline", 147, 198, 25, 11, 14, 0, 25, 18),
        new("IWGDF Offloading Guideline", 199, 239, 16, 16, 0, 0, 16, 11),
        new("IWGDF Wound Healing Guideline", 240, 291, 31, 31, 0, 0, 31, 29),
        new("IWGDF Charcot Guideline", 292, 331, 26, 22, 4, 0, 26, 8),
        new("IWGDF Methodology", 332, 349, 0, 0, 0, 0, 0, 0),
    ];

    public const int ListItems = 154;
    public const int GradeableUnits = 152;
    public const int ParentStems = 2;
    public const int GradePairs = 125;
    public const int BpsStatement = 26;
    public const int BpsRecommendation = 1;
    public const int BodyHeadings = 147;
    public const int RationaleBlocks = 101;
    public const int StatementsCompared = 140, StatementsDiffering = 67;
    public const int CaptionedFigures = 14, EmbeddedImages = 27;
    public const int CaptionedTables = 18;
    public const int FigureRecommendationLinks = 4;
    public static readonly (int P50, int P90, int P95, int Max, int Min) StatementTokens = (55, 89, 121, 170, 24);

    public const int TopLevelNumbers = 137;
    public const int StatementTokensMean = 60;
    public static readonly (int P50, int P90, int P95, int Max, int Min, int Mean) StatementTokensNoMarker = (49, 85, 115, 166, 18, 54);
    public static readonly (int P50, int P90, int P95, int Max) RationaleTokens = (562, 1608, 2082, 3449);
    public const int JpegImages = 17, PngImages = 10;
    public const int ImagesOnFigurePages = 14, ImagesOnTitlePages = 9, ImagesElsewhere = 4;
    public const int FiguresUnanchorable = 8;
    public const int FigureRefsInStatements = 0;
    public const int ProvenanceStrings = 1;
    public const int FigureXrefDistinct = 23, FigureXrefTotal = 30;
    public const int TableXrefDistinct = 18, TableXrefTotal = 42;
    public const int FooterPagesWithDigits = 0;
    public const int RasterisedTables = 2;
}
