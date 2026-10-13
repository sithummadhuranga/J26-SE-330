// The C# A1, run on the real PDF, must reproduce every A1Expected number exactly. Skipped where the PDF is absent.

using IngestionPipeline;
using IngestionPipeline.Steps;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Tests;

// Runs A1 once per class. xUnit 2 can build the fixture even when every test is skipped, so it checks first.
public sealed class A1Fixture
{
    private readonly A1Result? _result;

    public A1Fixture()
    {
        if (CorpusFiles.MissingReason() is not null) return;
        var paths = RepoPaths.Discover();
        _result = A1Recon.Measure(paths.SourcePdf, WordPieceCounter.Load(paths.Vocabulary));
    }

    public A1Result Get() => _result ?? throw new InvalidOperationException("A1 was not measured: corpus files are missing.");
}

public class A1ParityTests(A1Fixture fixture) : IClassFixture<A1Fixture>
{
    [CorpusFact]
    public void Source_pdf_is_the_one_the_oracle_measured() =>
        Assert.Equal(A1Expected.SourcePdfSha256, fixture.Get().SourceSha256);

    [CorpusFact]
    public void Page_count() => Assert.Equal(A1Expected.Pages, fixture.Get().Pages);

    [CorpusFact]
    public void Nine_sub_guidelines_with_the_oracles_names_and_page_ranges() =>
        Assert.Equal(
            A1Expected.Rows.Select(r => (r.Name, r.FirstPage, r.LastPage)),
            fixture.Get().Rows.Select(r => (r.Name, r.FirstPage, r.LastPage)));

    [CorpusFact]
    public void Every_per_sub_guideline_count_matches() =>
        Assert.Equal(A1Expected.Rows, fixture.Get().Rows);

    [CorpusFact]
    public void List_items_gradeable_units_and_parent_stems()
    {
        var r = fixture.Get();
        Assert.Equal((A1Expected.ListItems, A1Expected.GradeableUnits, A1Expected.ParentStems),
            (r.ListItems, r.GradeableUnits, r.ParentStems));
    }

    [CorpusFact]
    public void Grade_pairs() => Assert.Equal(A1Expected.GradePairs, fixture.Get().GradePairs);

    [CorpusFact]
    public void Best_practice_statement_and_recommendation_markers()
    {
        var r = fixture.Get();
        Assert.Equal((A1Expected.BpsStatement, A1Expected.BpsRecommendation), (r.BpsStatement, r.BpsRecommendation));
    }

    [CorpusFact]
    public void Body_recommendation_headings() => Assert.Equal(A1Expected.BodyHeadings, fixture.Get().BodyHeadings);

    [CorpusFact]
    public void Rationale_blocks() => Assert.Equal(A1Expected.RationaleBlocks, fixture.Get().RationaleBlocks);

    [CorpusFact]
    public void List_and_body_wording_differ_for_67_of_140()
    {
        var r = fixture.Get();
        Assert.Equal((A1Expected.StatementsDiffering, A1Expected.StatementsCompared),
            (r.StatementsDiffering, r.StatementsCompared));
    }

    [CorpusFact]
    public void Captioned_figures_and_embedded_images()
    {
        var r = fixture.Get();
        Assert.Equal((A1Expected.CaptionedFigures, A1Expected.EmbeddedImages), (r.CaptionedFigures, r.EmbeddedImages));
    }

    [CorpusFact]
    public void Captioned_tables() => Assert.Equal(A1Expected.CaptionedTables, fixture.Get().CaptionedTables);

    [CorpusFact]
    public void Figure_to_recommendation_cross_references() =>
        Assert.Equal(A1Expected.FigureRecommendationLinks, fixture.Get().FigureLinks.Count);

    [CorpusFact]
    public void Statement_token_distribution()
    {
        var t = fixture.Get().StatementTokens;
        Assert.Equal(A1Expected.StatementTokens, (t.P50, t.P90, t.P95, t.Max, t.Min));
    }

    [CorpusFact]
    public void Every_check_the_a1_command_prints_passes()
    {
        var failed = A1Command.Compare(fixture.Get()).Where(c => !c.Pass)
            .Select(c => $"{c.Quantity}: oracle {c.Expected}, C# {c.Actual}").ToList();
        Assert.Empty(failed);
    }
}
