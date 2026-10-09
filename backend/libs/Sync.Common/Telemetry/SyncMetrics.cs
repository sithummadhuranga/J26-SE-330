using System.Diagnostics.Metrics;

namespace Sync.Common.Telemetry;

/// <summary>Pipeline metrics for Prometheus and the Grafana dashboard.</summary>
public static class SyncMetrics
{
    public const string MeterName = "MelaninWoundCdss";
    private static readonly Meter Meter = new(MeterName);

    /// <summary>Per-event push results at the gateway: ACCEPTED, DUPLICATE (a resend absorbed), REJECTED.</summary>
    public static readonly Counter<long> EventsPushed =
        Meter.CreateCounter<long>("sync.events.pushed", "{event}", "Per-event push results");

    /// <summary>Persister outcomes: persisted, deduplicated (a redelivery absorbed), revision_conflict, dead_letter.</summary>
    public static readonly Counter<long> EventsPersisted =
        Meter.CreateCounter<long>("sync.events.persisted", "{event}", "Persister outcomes");

    public static readonly Histogram<double> PersistDuration =
        Meter.CreateHistogram<double>("sync.persist.duration", "s", "One persister transaction");

    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("sync.outbox.published", "{message}", "Outbox rows published to Kafka");

    /// <summary>Orchestrator run outcomes: Stored, AlreadyProcessed, Superseded, Deferred, DeadLetter, Failed.</summary>
    public static readonly Counter<long> OrchestrationOutcomes =
        Meter.CreateCounter<long>("sync.orchestration.outcomes", "{run}", "Orchestrator workflow outcomes");

    /// <summary>Housekeeping (§9.4) per table: outbox (deleted), change_log (archived), inbox (deleted).</summary>
    public static readonly Counter<long> HousekeepingRows =
        Meter.CreateCounter<long>("sync.housekeeping.rows", "{row}", "Rows removed by housekeeping");

    public static readonly Histogram<double> OrchestrationDuration =
        Meter.CreateHistogram<double>("sync.orchestration.duration", "s", "One orchestrator workflow run");
}
