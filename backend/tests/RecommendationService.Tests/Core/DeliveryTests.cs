// Which passages are delivered, in which section and under which tag (decisions 37, 39): template order, then each
// query's stored rerank order, first place kept; grouped by section; tagged S1..Sn in that order.

using WoundRag.Core.Composition;
using static RecommendationService.Tests.Core.CompositionFixtures;

namespace RecommendationService.Tests.Core;

public class DeliveryTests
{
    private static readonly Dictionary<string, Passage> Passages = new[]
    {
        Recommendation("A-R01"), Recommendation("A-R02", "PROHIBIT"), Recommendation("A-R03"), Row("A-T1.1", "A-T1"),
    }.ToDictionary(p => p.ChunkId);

    private static readonly SectionPlacement Sections = Placement(
        ("A-R01", "assessmentInstructions"), ("A-R02", "contraindications"), ("A-R03", "management"), ("A-T1", "management"));

    [Fact]
    public void Passages_are_grouped_by_section_in_order_and_tagged_in_sequence()
    {
        var delivered = Delivery.Deliver(Outcome(["A-R01", "A-R03"], ["A-T1.1", "A-R02", "A-R03"]), Passages, Sections);
        Assert.Equal(["A-R03", "A-T1.1", "A-R02", "A-R01"], delivered.Select(d => d.Passage.ChunkId));
        Assert.Equal(["S1", "S2", "S3", "S4"], delivered.Select(d => d.Tag));
        Assert.Equal(["management", "management", "contraindications", "assessmentInstructions"], delivered.Select(d => d.Section));
    }

    [Fact]
    public void A_table_row_goes_where_its_table_goes() =>
        Assert.Equal("management", Assert.Single(Delivery.Deliver(Outcome(["A-T1.1"]), Passages, Sections)).Section);

    [Fact]
    public void A_passage_the_file_does_not_place_is_never_placed_by_default()
    {
        var unplaced = Placement(("A-R01", "management"));
        var e = Assert.Throws<InvalidOperationException>(() => Delivery.Deliver(Outcome(["A-R03"]), Passages, unplaced));
        Assert.Contains("A-R03", e.Message);
    }

    [Fact]
    public void A_prohibition_placed_outside_contraindications_is_refused()
    {
        // Safety invariant 5: a PROHIBIT passage renders only under contraindications.
        var wrong = Placement(("A-R02", "management"));
        Assert.Throws<InvalidOperationException>(() => Delivery.Deliver(Outcome(["A-R02"]), Passages, wrong));
    }

    [Fact]
    public void The_prompt_lists_every_heading_with_its_tagged_stored_text()
    {
        var delivered = Delivery.Deliver(Outcome(["A-R01", "A-R02"]), Passages, Sections);
        var sections = Delivery.PromptSections(delivered);
        Assert.Equal(["management", "contraindications", "trajectory", "assessmentInstructions"], sections.Select(s => s.Heading));
        Assert.Equal(("S1", "[A-R02 header]\nbody of A-R02"), (sections[1].Passages[0].Tag, sections[1].Passages[0].Text));
        Assert.Empty(sections[0].Passages);
    }

    [Fact]
    public void Nothing_admissible_delivers_nothing() => Assert.Empty(Delivery.Deliver(Outcome([]), Passages, Sections));
}
