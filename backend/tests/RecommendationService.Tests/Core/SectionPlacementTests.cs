// Only an author-confirmed placement written for the served corpus version may be used (decision 37).

using WoundRag.Core.Composition;
using static RecommendationService.Tests.Core.CompositionFixtures;

namespace RecommendationService.Tests.Core;

public class SectionPlacementTests
{
    [Fact]
    public void A_confirmed_file_for_the_served_version_is_usable() => Assert.Null(Placement().WhyNotUsable("test-1.r1"));

    [Fact]
    public void An_unconfirmed_file_is_not_usable() =>
        Assert.Contains("not confirmed", (Placement() with { Status = "proposed" }).WhyNotUsable("test-1.r1"));

    [Fact]
    public void A_file_for_another_version_is_not_usable() =>
        Assert.Contains("test-1.r1", Placement().WhyNotUsable("test-1.r2"));
}
