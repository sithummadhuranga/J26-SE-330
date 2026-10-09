using Confluent.Kafka;
using Orchestrator.Graph;
using Sync.Common.Kafka;

namespace Orchestrator.Consumers;

/// <summary>Handles the delayed retry topics; failures move one hop closer to the DLQ.</summary>
public sealed class RetryTopicsConsumer(
    WorkflowRunner runner, OutcomeRouter router, IConfiguration config, ILogger<RetryTopicsConsumer> logger)
    : BackgroundService
{
    public static readonly string[] RetryTopics = [Topics.Retry30s, Topics.Retry5m];

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    /// <summary>A retry topic's delay; tests override it with Retry:FirstDelaySeconds/SecondDelaySeconds.</summary>
    public TimeSpan DelayFor(string topic) => topic switch
    {
        Topics.Retry30s when config.GetValue<double?>("Retry:FirstDelaySeconds") is { } s => TimeSpan.FromSeconds(s),
        Topics.Retry5m when config.GetValue<double?>("Retry:SecondDelaySeconds") is { } s => TimeSpan.FromSeconds(s),
        _ => RetryRouting.DelayFor(topic),
    };

    /// <summary>When a message read from <paramref name="topic"/> may be processed.</summary>
    public static DateTimeOffset DueAt(Timestamp timestamp, TimeSpan delay) =>
        DateTimeOffset.FromUnixTimeMilliseconds(timestamp.UnixTimestampMs) + delay;

    private async Task RunAsync(CancellationToken ct)
    {
        var bootstrap = config["Kafka:BootstrapServers"] ?? KafkaDefaults.DefaultBootstrapServers;
        var paused = new Dictionary<TopicPartition, DateTimeOffset>();

        using var consumer = new ConsumerBuilder<string, byte[]>(
                KafkaDefaults.Consumer(bootstrap, ConsumerGroups.OrchestratorRetry))
            // A partition we paused and then lost in a rebalance must not stay on our list.
            .SetPartitionsRevokedHandler((_, revoked) => { foreach (var p in revoked) paused.Remove(p.TopicPartition); })
            .Build();
        consumer.Subscribe(RetryTopics);
        logger.LogInformation("Retry consumer on {Topics} as group {Group} (delays {First} / {Second})",
            string.Join(", ", RetryTopics), ConsumerGroups.OrchestratorRetry, DelayFor(Topics.Retry30s), DelayFor(Topics.Retry5m));

        while (!ct.IsCancellationRequested)
        {
            ResumeDuePartitions(consumer, paused);

            ConsumeResult<string, byte[]>? result;
            try
            {
                // Short timeout so we wake up to resume partitions and stay in the consumer group.
                result = consumer.Consume(TimeSpan.FromSeconds(1));
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex)
            {
                logger.LogWarning(ex, "Consume failed; retrying");
                continue;
            }
            if (result is null) continue;

            var due = DueAt(result.Message.Timestamp, DelayFor(result.Topic));
            if (due > DateTimeOffset.UtcNow)
            {
                // Messages in a partition are in timestamp order, so nothing behind this one is due either.
                consumer.Pause([result.TopicPartition]);
                consumer.Seek(result.TopicPartitionOffset);
                paused[result.TopicPartition] = due;
                continue;
            }

            try
            {
                await PersistedEventsConsumer.ProcessAsync(result, runner, router, logger, ct);
                consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not finish {Ref}; will redeliver", result.TopicPartitionOffset.KafkaRef());
                consumer.Seek(result.TopicPartitionOffset);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        consumer.Close();
    }

    private static void ResumeDuePartitions(IConsumer<string, byte[]> consumer, Dictionary<TopicPartition, DateTimeOffset> paused)
    {
        var now = DateTimeOffset.UtcNow;
        var ready = paused.Where(p => p.Value <= now).Select(p => p.Key).ToList();
        if (ready.Count == 0) return;
        consumer.Resume(ready);
        foreach (var p in ready) paused.Remove(p);
    }
}
