using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Sync.Common.Kafka;

namespace Housekeeping;

/// <summary>Reads retention.ms for the topics whose consumers record messages in messaging.inbox.</summary>
public sealed class KafkaRetentionProbe(IAdminClient admin, ILogger<KafkaRetentionProbe> logger)
{
    /// <summary>The topics the orchestrator reads. The DLQ is included, because replayed dead letters get redelivered too.</summary>
    public static readonly string[] InboxTopics =
        [Topics.WoundEventsPersisted, Topics.Retry30s, Topics.Retry5m, Topics.DeadLetter];

    /// <returns>retention.ms for each topic, or null if we couldn't ask Kafka.</returns>
    public async Task<IReadOnlyCollection<long>?> TopicRetentionsAsync()
    {
        try
        {
            var results = await admin.DescribeConfigsAsync(
                InboxTopics.Select(t => new ConfigResource { Type = ResourceType.Topic, Name = t }),
                new DescribeConfigsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
            return results.Select(r => long.Parse(r.Entries["retention.ms"].Value)).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read topic retention from Kafka; inbox rows are kept this cycle");
            return null;
        }
    }
}
