// A clock that always reads the same time, so stored timestamps can be asserted exactly.

namespace RecommendationService.Tests.Fakes;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
