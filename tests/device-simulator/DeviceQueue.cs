using System.Text.Json.Nodes;

namespace DeviceSimulator;

/// <summary>A record's status on the phone (architecture §6.1).</summary>
public enum RecordStatus
{
    Pending,
    InFlight,
    Accepted,        // durable in Kafka: what "synced" means on the device
    Rejected,        // shown to the clinician, never retried automatically
    AdviceDeferred,
    Complete,        // advice delivered
    Superseded,      // a newer revision of the same assessment got the advice
}

/// <summary>One row of the device's wound_event_queue (§6), plus what the evaluation records about it.</summary>
public sealed class QueuedEvent(JsonObject payload)
{
    public JsonObject Payload { get; } = payload;
    public Guid EventId => Guid.Parse((string)Payload["eventId"]!);
    public Guid AssessmentId => Guid.Parse((string)Payload["assessmentId"]!);
    public int Revision => (int)Payload["revision"]!;
    public int SizeBytes { get; } = System.Text.Encoding.UTF8.GetByteCount(payload.ToJsonString());

    public RecordStatus Status { get; set; } = RecordStatus.Pending;
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public DateTimeOffset EnqueuedAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsFinal => Status is RecordStatus.Rejected or RecordStatus.Complete or RecordStatus.Superseded;
}

/// <summary>A thread-safe offline queue. Leased rows go back to pending if the lease runs out.</summary>
public sealed class DeviceQueue(TimeProvider clock)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    public const int MaxBatchEvents = 50;
    public const int MaxBatchBytes = 256 * 1024;

    private readonly List<QueuedEvent> _rows = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<QueuedEvent> Snapshot()
    {
        lock (_lock) return _rows.ToList();
    }

    public QueuedEvent Enqueue(JsonObject payload)
    {
        var row = new QueuedEvent(payload) { EnqueuedAt = clock.GetUtcNow() };
        lock (_lock) _rows.Add(row);
        return row;
    }

    /// <summary>Leases the next batch, oldest first, with at most 50 events and 256 KB. Expired leases count as PENDING.</summary>
    public IReadOnlyList<QueuedEvent> LeaseBatch(int maxEvents = MaxBatchEvents)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            var batch = new List<QueuedEvent>();
            var bytes = 0;
            foreach (var row in _rows)
            {
                var ready = row.Status == RecordStatus.Pending
                            || (row.Status == RecordStatus.InFlight && row.LeaseExpiresAt <= now);
                if (!ready) continue;
                if (batch.Count == maxEvents || (batch.Count > 0 && bytes + row.SizeBytes > MaxBatchBytes)) break;
                batch.Add(row);
                bytes += row.SizeBytes;
            }
            foreach (var row in batch)
            {
                row.Status = RecordStatus.InFlight;
                row.LeaseExpiresAt = now + Lease;
                row.Attempts++;
            }
            return batch;
        }
    }

    /// <summary>For a push that failed or got no answer. The rows go back to PENDING, which is safe because sending an event twice does no harm.</summary>
    public void Release(IEnumerable<QueuedEvent> rows, string error)
    {
        lock (_lock)
            foreach (var row in rows.Where(r => r.Status == RecordStatus.InFlight))
            {
                row.Status = RecordStatus.Pending;
                row.LeaseExpiresAt = null;
                row.LastError = error;
            }
    }

    /// <summary>Applies the push result for one event (§7.1). DUPLICATE is treated just like ACCEPTED.</summary>
    public void ApplyPushResult(QueuedEvent row, string status, string? code)
    {
        lock (_lock)
        {
            row.LeaseExpiresAt = null;
            if (status is "ACCEPTED" or "DUPLICATE")
            {
                if (row.Status == RecordStatus.InFlight) row.Status = RecordStatus.Accepted;
                row.AcceptedAt ??= clock.GetUtcNow();
            }
            else
            {
                row.Status = RecordStatus.Rejected;
                row.LastError = code;
            }
        }
    }

    /// <summary>Applies one pulled change by (assessmentId, revision). Getting the same change twice does no harm.</summary>
    public void ApplyChange(Guid assessmentId, int revision, string type)
    {
        lock (_lock)
        {
            var row = _rows.FirstOrDefault(r => r.AssessmentId == assessmentId && r.Revision == revision);
            if (row is null || row.IsFinal) return;
            switch (type)
            {
                case "ADVICE_DEFERRED":
                    if (row.Status is RecordStatus.Accepted or RecordStatus.InFlight or RecordStatus.Pending)
                        row.Status = RecordStatus.AdviceDeferred;
                    break;
                case "RECOMMENDATION_READY":
                    row.Status = RecordStatus.Complete;
                    row.CompletedAt = clock.GetUtcNow();
                    row.AcceptedAt ??= row.CompletedAt;
                    break;
                case "SUPERSEDED":
                    row.Status = RecordStatus.Superseded;
                    row.CompletedAt = clock.GetUtcNow();
                    row.AcceptedAt ??= row.CompletedAt;
                    break;
            }
        }
    }

    public bool HasWorkToSend()
    {
        var now = clock.GetUtcNow();
        lock (_lock)
            return _rows.Any(r => r.Status == RecordStatus.Pending
                                  || (r.Status == RecordStatus.InFlight && r.LeaseExpiresAt <= now));
    }

    public bool AllFinal()
    {
        lock (_lock) return _rows.All(r => r.IsFinal);
    }
}
