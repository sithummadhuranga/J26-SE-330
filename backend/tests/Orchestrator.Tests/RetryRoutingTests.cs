using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrator.Consumers;
using Sync.Common.Kafka;

namespace Orchestrator.Tests;

/// <summary>The §8.3 retry chain: where a failed message goes next, and when it may be tried again.</summary>
public class RetryRoutingTests
{
    [Theory]
    [InlineData(Topics.WoundEventsPersisted, Topics.Retry30s)]
    [InlineData(Topics.Retry30s, Topics.Retry5m)]
    [InlineData(Topics.Retry5m, Topics.DeadLetter)]
    public void Each_failure_moves_one_hop_further(string from, string to) =>
        Assert.Equal(to, RetryRouting.NextHop(from));

    private static RetryTopicsConsumer Consumer(Dictionary<string, string?> settings) =>
        new(null!, null!, new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<RetryTopicsConsumer>.Instance);

    [Fact]
    public void Default_delays_are_30_seconds_and_5_minutes()
    {
        var consumer = Consumer([]);
        Assert.Equal(TimeSpan.FromSeconds(30), consumer.DelayFor(Topics.Retry30s));
        Assert.Equal(TimeSpan.FromMinutes(5), consumer.DelayFor(Topics.Retry5m));
    }

    [Fact]
    public void Delays_can_be_shortened_for_tests()
    {
        var consumer = Consumer(new() { ["Retry:FirstDelaySeconds"] = "2", ["Retry:SecondDelaySeconds"] = "4" });
        Assert.Equal(TimeSpan.FromSeconds(2), consumer.DelayFor(Topics.Retry30s));
        Assert.Equal(TimeSpan.FromSeconds(4), consumer.DelayFor(Topics.Retry5m));
    }

    [Fact]
    public void Due_time_counts_from_the_message_timestamp_not_from_when_it_is_read()
    {
        var produced = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var due = RetryTopicsConsumer.DueAt(new Timestamp(produced), TimeSpan.FromSeconds(30));
        Assert.Equal(produced.AddSeconds(30), due);
    }
}
