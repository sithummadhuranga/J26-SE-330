// The structure check, run on the real PDF, must reproduce every StructureBaseline number exactly. Skipped where the PDF is absent.

using IngestionPipeline;
using IngestionPipeline.Pdf;
using IngestionPipeline.Steps;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Tests;

// Measures the PDF once per class. xUnit 2 can build the fixture even when every test is skipped, so it checks first.
public sealed class StructureFixture
{
    private readonly PdfStructureCounts? _result;

    public StructureFixture()
    {
        if (CorpusFiles.MissingReason() is not null) return;
        var paths = RepoPaths.Discover();
        _result = PdfStructure.Measure(paths.SourcePdf, WordPieceCounter.Load(paths.Vocabulary));
    }

    public PdfStructureCounts Get() => _result ?? throw new InvalidOperationException("the PDF was not measured: corpus files are missing.");
}

public class StructureBaselineTests(StructureFixture fixture) : IClassFixture<StructureFixture>
{
    [CorpusFact]
    public void Source_pdf_is_the_one_the_oracle_measured() =>
        Assert.Equal(StructureBaseline.SourcePdfSha256, fixture.Get().SourceSha256);

    [CorpusFact]
    public void Page_count() => Assert.Equal(StructureBaseline.Pages, fixture.Get().Pages);

    [CorpusFact]
    public void Nine_sub_guidelines_with_the_oracles_names_and_page_ranges() =>
        Assert.Equal(
            StructureBaseline.Rows.Select(r => (r.Name, r.FirstPage, r.LastPage)),
            fixture.Get().Rows.Select(r => (r.Name, r.FirstPage, r.LastPage)));

    [CorpusFact]
    public void Every_per_sub_guideline_count_matches() =>
        Assert.Equal(StructureBaseline.Rows, fixture.Get().Rows);

    [CorpusFact]
    public void List_items_gradeable_units_and_parent_stems()
    {
        var r = fixture.Get();
        Assert.Equal((StructureBaseline.ListItems, StructureBaseline.GradeableUnits, StructureBaseline.ParentStems),
            (r.ListItems, r.GradeableUnits, r.ParentStems));
    }

    [CorpusFact]
    public void Grade_pairs() => Assert.Equal(StructureBaseline.GradePairs, fixture.Get().GradePairs);

    [CorpusFact]
    public void Best_practice_statement_and_recommendation_markers()
    {
        var r = fixture.Get();
        Assert.Equal((StructureBaseline.BpsStatement, StructureBaseline.BpsRecommendation), (r.BpsStatement, r.BpsRecommendation));
    }

    [CorpusFact]
    public void Body_recommendation_headings() => Assert.Equal(StructureBaseline.BodyHeadings, fixture.Get().BodyHeadings);

    [CorpusFact]
    public void Rationale_blocks() => Assert.Equal(StructureBaseline.RationaleBlocks, fixture.Get().RationaleBlocks);

    [CorpusFact]
    public void List_and_body_wording_differ_for_67_of_140()
    {
        var r = fixture.Get();
        Assert.Equal((StructureBaseline.StatementsDiffering, StructureBaseline.StatementsCompared),
            (r.StatementsDiffering, r.StatementsCompared));
    }

    [CorpusFact]
    public void Captioned_figures_and_embedded_images()
    {
        var r = fixture.Get();
        Assert.Equal((StructureBaseline.CaptionedFigures, StructureBaseline.EmbeddedImages), (r.CaptionedFigures, r.EmbeddedImages));
    }

    [CorpusFact]
    public void Captioned_tables() => Assert.Equal(StructureBaseline.CaptionedTables, fixture.Get().CaptionedTables);

    [CorpusFact]
    public void Figure_to_recommendation_cross_references() =>
        Assert.Equal(StructureBaseline.FigureRecommendationLinks, fixture.Get().FigureLinks.Count);

    [CorpusFact]
    public void Statement_token_distribution()
    {
        var t = fixture.Get().StatementTokens;
        Assert.Equal(StructureBaseline.StatementTokens, (t.P50, t.P90, t.P95, t.Max, t.Min));
    }

    [CorpusFact]
    public void Every_check_the_a1_command_prints_passes()
    {
        var failed = StructureCheckCommand.Compare(fixture.Get()).Where(c => !c.Pass)
            .Select(c => $"{c.Quantity}: oracle {c.Expected}, C# {c.Actual}").ToList();
        Assert.Empty(failed);
    }
}
