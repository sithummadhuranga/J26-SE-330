// Reads and writes figures.yaml: one entry per figure and table image, with hashes and links. The Header
// text stays as written: it is part of the file whose SHA-256 the r1 manifest records.

using System.Text;

namespace IngestionPipeline.Corpus;

public sealed class FiguresFileDto
{
    public string Status { get; set; } = "proposed";
    public string? ConfirmedBy { get; set; }
    public string? ConfirmedOn { get; set; }
    public string SourcePdfSha256 { get; set; } = "";
    public string Generated { get; set; } = "";
    public Dictionary<string, string> ExtraCodes { get; set; } = [];
    public List<FigureDto> Figures { get; set; } = [];
}

public sealed class FigureDto
{
    public string FigureId { get; set; } = "";
    public string Label { get; set; } = "";
    public string SubGuideline { get; set; } = "";
    public int PdfIndex { get; set; }
    public string MediaType { get; set; } = "";
    public string SourceStreamSha256 { get; set; } = "";
    public string FigureSha256 { get; set; } = "";
    public List<FigureLinkDto> Links { get; set; } = [];
}

public sealed class FigureLinkDto
{
    public string ChunkId { get; set; } = "";
    public string SourceField { get; set; } = "";
    public string TextChunkId { get; set; } = "";
}

public static class FiguresFile
{
    public static readonly Dictionary<string, string> ProposedExtraCodes = new() { ["IWGDF Practical Guidelines"] = "PRA" };

    private const string Header = """
        # figures.yaml — every guideline figure and table image (PLAN.md §M4 A5; decisions 4, 22).
        # Written by `ingestion-pipeline -- propose-figures`: IDs, pages, hashes and links. The
        # author confirms the file (status, confirmed_by, confirmed_on); re-running keeps those.
        # Figures are never served (author, 4 Oct 2026): a linked recommendation names the figure
        # and its page, and the image itself stays in the PDF. No guideline text is stored here.

        """;

    public static FiguresFileDto? Read(string path) =>
        File.Exists(path) ? AliasFile.Deserializer().Deserialize<FiguresFileDto>(File.ReadAllText(path)) : null;

    public static void Write(string path, FiguresFileDto dto) =>
        File.WriteAllText(path, Header + AliasFile.Serializer().Serialize(dto), new UTF8Encoding(false));
}
