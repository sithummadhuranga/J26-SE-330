using System.Text.Json;
using Confluent.Kafka;
using Orchestrator.Graph;
using Sync.Common.Contracts;
using Sync.Common.Kafka;
using Sync.Common.Telemetry;
using System.Diagnostics;

namespace Orchestrator.Consumers;

/// <summary>Runs the workflow for each persisted event, committing once the outcome is durable.</summary>
public sealed class PersistedEventsConsumer(
    WorkflowRunner runner, OutcomeRouter router, IConfiguration config, ILogger<PersistedEventsConsumer> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        var bootstrap = config["Kafka:BootstrapServers"] ?? KafkaDefaults.DefaultBootstrapServers;
        var concurrency = config.GetValue("Orchestrator:MaxConcurrency", 6);
        var workers = new PartitionWorkers((r, token) => HandleAsync(r, token), concurrency,
            maxQueuedPerPartition: 50, logger, ct);

        using var consumer = new ConsumerBuilder<string, byte[]>(
                KafkaDefaults.Consumer(bootstrap, ConsumerGroups.Orchestrator))
            .SetPartitionsRevokedHandler((c, revoked) => workers.Stop(c, revoked.Select(p => p.TopicPartition), commit: true))
            .SetPartitionsLostHandler((c, lost) => workers.Stop(c, lost.Select(p => p.TopicPartition), commit: false))
            .Build();
        consumer.Subscribe(Topics.WoundEventsPersisted);
        logger.LogInformation("Orchestrator consuming {Topic} as group {Group}, up to {Concurrency} at once",
            Topics.WoundEventsPersisted, ConsumerGroups.Orchestrator, concurrency);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Short poll so finished work is committed and drained partitions resume promptly.
                if (consumer.Consume(TimeSpan.FromMilliseconds(200)) is { } result) workers.Dispatch(consumer, result);
                workers.Tick(consumer);
            }
            catch (ConsumeException ex)
            {
                logger.LogWarning(ex, "Consume failed; retrying");
            }
            catch (KafkaException ex)
            {
                logger.LogWarning(ex, "Commit failed; will retry on the next tick");
            }
        }

        workers.Stop(consumer, workers.Partitions, commit: true);
        consumer.Close();
    }

    /// <summary>Shared with the retry consumer: parse, run the workflow, route the outcome.</summary>
    internal static async Task<OrchestrationOutcome> ProcessAsync(ConsumeResult<string, byte[]> result, WorkflowRunner runner,
        OutcomeRouter router, ILogger logger, CancellationToken ct)
    {
        using var activity = SyncTelemetry.StartConsume(SyncTelemetry.Orchestrator, "orchestrate", result.Message.Headers);

        PersistedEvent evt;
        try
        {
            evt = JsonSerializer.Deserialize<PersistedEvent>(result.Message.Value, JsonSerializerOptions.Web)
                  ?? throw new JsonException("empty payload");
        }
        catch (JsonException ex)
        {
            // Unparseable: retrying cannot help.
            var unknown = new OrchestrationOutcome(new PersistedEvent(Guid.Empty, Guid.Empty, 0, Guid.Empty),
                OutcomeKind.DeadLetter, $"UNPARSEABLE: {ex.Message}");
            await router.RouteAsync(result, unknown, ct);
            return unknown;
        }

        var job = new OrchestrationJob(evt, result.Message.Headers.GetHeader(HeaderNames.TraceParent));
        var started = Stopwatch.GetTimestamp();
        var outcome = await runner.RunAsync(job, ct);
        await router.RouteAsync(result, outcome, ct);
        SyncMetrics.OrchestrationDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        SyncMetrics.OrchestrationOutcomes.Add(1, new KeyValuePair<string, object?>("kind", outcome.Kind.ToString()));

        logger.Log(outcome.Retry || outcome.Kind == OutcomeKind.DeadLetter ? LogLevel.Warning : LogLevel.Information,
            "{EventId} (assessment {AssessmentId} rev {Revision}) from {Ref}: {Kind} {Detail}", evt.EventId,
            evt.AssessmentId, evt.Revision, result.TopicPartitionOffset.KafkaRef(), outcome.Kind, outcome.Detail);
        return outcome;
    }

    private Task HandleAsync(ConsumeResult<string, byte[]> result, CancellationToken ct) =>
        ProcessAsync(result, runner, router, logger, ct);
}
