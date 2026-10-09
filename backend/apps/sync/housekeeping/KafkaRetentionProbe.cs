using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Sync.Common.Kafka;

namespace Housekeeping;

/// <summary>Reads retention.ms of the topics whose consumers record messages in messaging.inbox.</summary>
public sealed class KafkaRetentionProbe(IAdminClient admin, ILogger<KafkaRetentionProbe> logger)
{
    /// <summary>Topics the orchestrator reads, including the DLQ since replayed dead letters are redelivered too.</summary>
    public static readonly string[] InboxTopics =
        [Topics.WoundEventsPersisted, Topics.Retry30s, Topics.Retry5m, Topics.DeadLetter];

    /// <returns>retention.ms per topic, or null if Kafka could not be asked.</returns>
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
