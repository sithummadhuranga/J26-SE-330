// Extractive mode: each passage's own body, cited by its own tag, at most the first 5 per section in delivery order
// (decision 39 amendment).

using WoundRag.Core.Composition;
using static RecommendationService.Tests.Core.CompositionFixtures;

namespace RecommendationService.Tests.Core;

public class ExtractiveAnswerTests
{
    [Fact]
    public void Each_item_is_a_passage_body_cited_by_its_own_tag_and_a_section_shows_at_most_five()
    {
        var ids = Enumerable.Range(1, 7).Select(i => $"A-R{i:00}").ToArray();
        var passages = ids.ToDictionary(id => id, id => Recommendation(id));
        var placement = Placement([.. ids.Select(id => (id, id == "A-R07" ? "assessmentInstructions" : "management"))]);
        var delivered = Delivery.Deliver(Outcome(ids), passages, placement);

        var items = ExtractiveAnswer.Items(delivered);
        Assert.Equal(["body of A-R01", "body of A-R02", "body of A-R03", "body of A-R04", "body of A-R05"], items["management"].Select(i => i.Text));
        Assert.Equal(["S1"], items["management"][0].Cite);
        Assert.Equal(["S7"], Assert.Single(items["assessmentInstructions"]).Cite);
        Assert.Empty(items["contraindications"]);
    }
}
