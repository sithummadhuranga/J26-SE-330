using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Orchestrator.Graph;
using Orchestrator.Persistence;
using Sync.Common.Kafka;

namespace Orchestrator.Consumers;

/// <summary>Makes a run's outcome durable (retry topic, DLQ or nothing) before the offset is committed.</summary>
public sealed class OutcomeRouter(IProducer<string, byte[]> producer, IOrchestratorStore store, ILogger<OutcomeRouter> logger)
{
    public async Task RouteAsync(ConsumeResult<string, byte[]> source, OrchestrationOutcome outcome, CancellationToken ct)
    {
        if (!outcome.Retry && outcome.Kind != OutcomeKind.DeadLetter) return;

        try
        {
            // Tell the device advice is deferred; best effort, the retry still goes ahead if this fails.
            await store.RecordDeferredAsync(outcome.Event, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record ADVICE_DEFERRED for {EventId}", outcome.Event.EventId);
        }

        var target = outcome.Kind == OutcomeKind.DeadLetter ? Topics.DeadLetter : RetryRouting.NextHop(source.Topic);
        var headers = new Headers();
        foreach (var h in source.Message.Headers ?? [])
            if (h.Key is not (RetryRouting.RetryCountHeader or RetryRouting.ErrorHeader))
                headers.Add(h.Key, h.GetValueBytes());

        var attempts = int.TryParse(source.Message.Headers.GetHeader(RetryRouting.RetryCountHeader), out var n) ? n : 0;
        headers.Add(RetryRouting.RetryCountHeader, Encoding.UTF8.GetBytes((attempts + 1).ToString(CultureInfo.InvariantCulture)));
        headers.Add(RetryRouting.ErrorHeader, Encoding.UTF8.GetBytes($"{outcome.Kind}: {outcome.Detail}"));
        if (source.Message.Headers.GetHeader(RetryRouting.OriginalTopicHeader) is null)
            headers.Add(RetryRouting.OriginalTopicHeader, Encoding.UTF8.GetBytes(source.Topic));
        if (target == Topics.DeadLetter)
        {
            headers.Add("dlq-reason", Encoding.UTF8.GetBytes(outcome.Kind.ToString()));
            headers.Add("dlq-detail", Encoding.UTF8.GetBytes(outcome.Detail ?? ""));
            headers.Add("dlq-source", Encoding.UTF8.GetBytes(source.TopicPartitionOffset.KafkaRef()));
        }

        await producer.ProduceAsync(target,
            new Message<string, byte[]> { Key = source.Message.Key, Value = source.Message.Value, Headers = headers }, ct);
        logger.LogWarning("{EventId} {Kind} ({Detail}); sent to {Target}", outcome.Event.EventId, outcome.Kind, outcome.Detail, target);
    }
}
