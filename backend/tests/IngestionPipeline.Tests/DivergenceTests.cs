// The divergence report (decision 1): the word diff is exact, and the report covers exactly the chunks the
// chunker marks text_divergence: 34, 9 for clinician review (decision 23's addendum).

using IngestionPipeline;
using IngestionPipeline.Corpus;

namespace IngestionPipeline.Tests;

public class DivergenceTests(ChunkFixture fixture) : IClassFixture<ChunkFixture>
{
    [Fact]
    public void Diff_reports_exactly_the_unmatched_words_of_each_side()
    {
        var (a, b) = Divergence.Diff(Divergence.Words("Use a removable device daily"), Divergence.Words("Use a non-removable device"));
        Assert.Equal(["daily"], a.Select(w => w.Word));
        Assert.Equal(["non"], b.Select(w => w.Word));
    }

    [Fact]
    public void Identical_wordings_have_no_difference()
    {
        var (a, b) = Divergence.Diff(Divergence.Words("In a person, do X."), Divergence.Words("in a person do x"));
        Assert.Empty(a);
        Assert.Empty(b);
    }

    [CorpusFact]
    public void The_report_covers_exactly_the_chunks_marked_divergent()
    {
        var (_, chunks) = fixture.Get();
        var annotation = AnnotationFile.Read(Path.Combine(RepoPaths.Discover().Patterns, "annotation.yaml"))!;
        var rows = Divergence.Rows(chunks, new Annotator(annotation));
        Assert.Equal(chunks.Where(c => c.TextDivergence).Select(c => c.ChunkId), rows.Select(r => r.ChunkId));
        Assert.Equal(34, rows.Count);
        Assert.All(rows, r => Assert.True(r.OnlyInBody.Count + r.OnlyInList.Count > 0));
        Assert.Equal(9, rows.Count(r => r.ClinicianReview));
        Assert.Equal(0, rows.Count(r => r.TouchesNumber));
    }

    [CorpusFact]
    public void The_note_after_the_infection_list_is_not_part_of_INF_R25()
    {
        var (_, chunks) = fixture.Get();
        var r25 = chunks.Single(c => c.ChunkId == "INF-R25");
        Assert.DoesNotContain("Note:", r25.VariantText);
        Assert.False(r25.TextDivergence);
    }
}
