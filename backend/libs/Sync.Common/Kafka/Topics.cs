namespace Sync.Common.Kafka;

/// <summary>Topic names from architecture §8.1. Keep in sync with infra/kafka/create-topics.sh.</summary>
public static class Topics
{
    public const string WoundEvents = "wound-events";                     // key: woundId
    public const string WoundEventsPersisted = "wound-events.persisted";  // key: woundId
    public const string RecommendationsReady = "recommendations.ready";   // key: assessmentId
    public const string Retry30s = "wound-events.retry.30s";
    public const string Retry5m = "wound-events.retry.5m";
    public const string DeadLetter = "wound-events.dlq";
}

public static class ConsumerGroups
{
    public const string Persister = "persister";
    public const string Orchestrator = "orchestrator";
    /// <summary>Delayed retries (§8.3), kept apart so pausing for minutes never stalls the main orchestrator consumer.</summary>
    public const string OrchestratorRetry = "orchestrator-retry";
    public const string GatewayNotifier = "gateway-notifier";
}

public static class HeaderNames
{
    public const string EventId = "event-id";
    public const string SchemaVersion = "schema-version";
    public const string DeviceId = "device-id";
    public const string TraceParent = "traceparent";
}
