using System.Text;
using Confluent.Kafka;

namespace Sync.Common.Kafka;

/// <summary>The client settings that matter (architecture §8.2), in one place so no service drifts.</summary>
public static class KafkaDefaults
{
    public const string DefaultBootstrapServers = "localhost:29092";

    /// <param name="deliveryTimeoutMs">How long a produce waits for acks; short in the gateway for a quick 503.</param>
    public static ProducerConfig Producer(string bootstrapServers, int deliveryTimeoutMs = 30_000) => new()
    {
        BootstrapServers = bootstrapServers,
        Acks = Acks.All,
        EnableIdempotence = true,
        MessageTimeoutMs = deliveryTimeoutMs,
    };

    /// <summary>Offsets are committed by hand, only after the database transaction commits.</summary>
    public static ConsumerConfig Consumer(string bootstrapServers, string groupId) => new()
    {
        BootstrapServers = bootstrapServers,
        GroupId = groupId,
        EnableAutoCommit = false,
        AutoOffsetReset = AutoOffsetReset.Earliest,
    };

    public static string? GetHeader(this Confluent.Kafka.Headers? headers, string key) =>
        headers is not null && headers.TryGetLastBytes(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    public static string KafkaRef(this TopicPartitionOffset tpo) => $"{tpo.Topic}/{tpo.Partition.Value}/{tpo.Offset.Value}";
}
