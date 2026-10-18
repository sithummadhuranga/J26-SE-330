// A stored passage's body is its text without the context header line; a text without that line is never shown
// with the header glued on.

using WoundRag.Core.Composition;
using static RecommendationService.Tests.Core.CompositionFixtures;

namespace RecommendationService.Tests.Core;

public class PassageTests
{
    [Fact]
    public void The_body_drops_the_header_line() => Assert.Equal("body of A-R01", Recommendation("A-R01").Body);

    [Fact]
    public void A_text_that_does_not_start_with_its_header_line_throws()
    {
        var broken = Recommendation("A-R01") with { Text = "no header here" };
        Assert.Throws<InvalidOperationException>(() => broken.Body);
    }

    [Fact]
    public void A_row_is_placed_by_its_table_and_a_recommendation_by_itself()
    {
        Assert.Equal("A-T1", Row("A-T1.1", "A-T1").PlacementKey);
        Assert.Equal("A-R01", Recommendation("A-R01").PlacementKey);
    }
}
