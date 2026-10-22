using System.Diagnostics.Metrics;

namespace Sync.Common.Telemetry;

/// <summary>Pipeline metrics for Prometheus and the Grafana dashboard.</summary>
public static class SyncMetrics
{
    public const string MeterName = "MelaninWoundCdss";
    private static readonly Meter Meter = new(MeterName);

    /// <summary>The push result for each event at the gateway: ACCEPTED, DUPLICATE (a resend we absorbed) or REJECTED.</summary>
    public static readonly Counter<long> EventsPushed =
        Meter.CreateCounter<long>("sync.events.pushed", "{event}", "Per-event push results");

    /// <summary>What the persister did: persisted, deduplicated (a redelivery we absorbed), revision_conflict or dead_letter.</summary>
    public static readonly Counter<long> EventsPersisted =
        Meter.CreateCounter<long>("sync.events.persisted", "{event}", "Persister outcomes");

    public static readonly Histogram<double> PersistDuration =
        Meter.CreateHistogram<double>("sync.persist.duration", "s", "One persister transaction");

    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("sync.outbox.published", "{message}", "Outbox rows published to Kafka");

    /// <summary>How each orchestrator run ended: Stored, AlreadyProcessed, Superseded, Deferred, DeadLetter or Failed.</summary>
    public static readonly Counter<long> OrchestrationOutcomes =
        Meter.CreateCounter<long>("sync.orchestration.outcomes", "{run}", "Orchestrator workflow outcomes");

    /// <summary>Housekeeping (§9.4) for each table: outbox rows deleted, change_log rows archived and inbox rows deleted.</summary>
    public static readonly Counter<long> HousekeepingRows =
        Meter.CreateCounter<long>("sync.housekeeping.rows", "{row}", "Rows removed by housekeeping");

    public static readonly Histogram<double> OrchestrationDuration =
        Meter.CreateHistogram<double>("sync.orchestration.duration", "s", "One orchestrator workflow run");
}
