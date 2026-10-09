using System.Text.Json.Nodes;
using DeviceSimulator;

namespace DeviceSimulator.Tests;

/// <summary>The simulated device follows the §6 queue rules the real app must follow.</summary>
public class DeviceQueueTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly ManualClock Clock = new();

    private static JsonObject Event(int revision = 1, Guid? assessment = null, int padding = 0) => new()
    {
        ["eventId"] = Guid.CreateVersion7().ToString(),
        ["assessmentId"] = (assessment ?? Guid.CreateVersion7()).ToString(),
        ["revision"] = revision,
        ["pad"] = new string('x', padding),
    };

    [Fact]
    public void Batches_are_oldest_first_and_at_most_50_events()
    {
        var queue = new DeviceQueue(Clock);
        var rows = Enumerable.Range(0, 60).Select(_ => queue.Enqueue(Event())).ToList();

        var batch = queue.LeaseBatch();

        Assert.Equal(50, batch.Count);
        Assert.Equal(rows.Take(50), batch);
        Assert.All(batch, r => Assert.Equal(RecordStatus.InFlight, r.Status));
    }

    [Fact]
    public void Batches_stay_under_256_KB()
    {
        var queue = new DeviceQueue(Clock);
        for (var i = 0; i < 5; i++) queue.Enqueue(Event(padding: 100_000));

        var batch = queue.LeaseBatch();

        Assert.Equal(2, batch.Count);
        Assert.True(batch.Sum(r => r.SizeBytes) <= DeviceQueue.MaxBatchBytes);
    }

    [Fact]
    public void Leased_rows_are_not_sent_twice_until_the_lease_lapses()
    {
        var clock = new ManualClock();
        var queue = new DeviceQueue(clock);
        var row = queue.Enqueue(Event());

        Assert.Single(queue.LeaseBatch());
        Assert.Empty(queue.LeaseBatch());

        clock.Now += DeviceQueue.Lease + TimeSpan.FromSeconds(1); // the app was killed mid-sync (§6.2)
        Assert.Equal([row], queue.LeaseBatch());
        Assert.Equal(2, row.Attempts);
    }

    [Fact]
    public void A_failed_push_returns_rows_to_pending()
    {
        var queue = new DeviceQueue(Clock);
        var row = queue.Enqueue(Event());
        queue.Release(queue.LeaseBatch(), "HTTP 503");

        Assert.Equal(RecordStatus.Pending, row.Status);
        Assert.Equal("HTTP 503", row.LastError);
        Assert.True(queue.HasWorkToSend());
    }

    [Fact]
    public void Duplicate_counts_as_accepted_and_rejected_is_final()
    {
        var queue = new DeviceQueue(Clock);
        var dup = queue.Enqueue(Event());
        var bad = queue.Enqueue(Event());
        queue.LeaseBatch();

        queue.ApplyPushResult(dup, "DUPLICATE", null);
        queue.ApplyPushResult(bad, "REJECTED", "SCHEMA_INVALID");

        Assert.Equal(RecordStatus.Accepted, dup.Status);
        Assert.NotNull(dup.AcceptedAt);
        Assert.Equal(RecordStatus.Rejected, bad.Status);
        Assert.True(bad.IsFinal);
        Assert.False(queue.HasWorkToSend()); // REJECTED is never retried automatically (§6.1)
    }

    [Fact]
    public void Pulled_changes_move_a_record_to_complete_and_repeats_are_harmless()
    {
        var queue = new DeviceQueue(Clock);
        var row = queue.Enqueue(Event());
        queue.LeaseBatch();
        queue.ApplyPushResult(row, "ACCEPTED", null);

        queue.ApplyChange(row.AssessmentId, 1, "PERSISTED");
        Assert.Equal(RecordStatus.Accepted, row.Status);
        queue.ApplyChange(row.AssessmentId, 1, "ADVICE_DEFERRED");
        Assert.Equal(RecordStatus.AdviceDeferred, row.Status);
        queue.ApplyChange(row.AssessmentId, 1, "RECOMMENDATION_READY");
        var completed = row.CompletedAt;
        queue.ApplyChange(row.AssessmentId, 1, "RECOMMENDATION_READY"); // re-sent within the 60 s window

        Assert.Equal(RecordStatus.Complete, row.Status);
        Assert.Equal(completed, row.CompletedAt);
        Assert.True(queue.AllFinal());
    }

    [Fact]
    public void Only_the_superseded_revision_is_closed_by_a_superseded_change()
    {
        var queue = new DeviceQueue(Clock);
        var assessment = Guid.CreateVersion7();
        var r1 = queue.Enqueue(Event(1, assessment));
        var r2 = queue.Enqueue(Event(2, assessment));

        queue.ApplyChange(assessment, 1, "SUPERSEDED");

        Assert.Equal(RecordStatus.Superseded, r1.Status);
        Assert.Equal(RecordStatus.Pending, r2.Status);
    }
}

public class BackoffTests
{
    [Fact]
    public void Starts_at_two_seconds_doubles_and_caps_at_five_minutes()
    {
        // Random that always returns its maximum shows the ceiling of each full-jitter draw.
        var backoff = new Backoff(new MaxRandom());
        var ceilings = Enumerable.Range(0, 12).Select(_ => backoff.NextDelay().TotalSeconds).ToList();

        Assert.Equal(2, ceilings[0], 0.01);
        Assert.Equal(4, ceilings[1], 0.01);
        Assert.Equal(8, ceilings[2], 0.01);
        Assert.Equal(300, ceilings[^1], 0.01);
        Assert.All(ceilings, c => Assert.True(c <= 300.01));
    }

    [Fact]
    public void Full_jitter_stays_under_the_ceiling()
    {
        var backoff = new Backoff(new Random(1));
        for (var i = 0; i < 50; i++)
            Assert.InRange(backoff.NextDelay(), TimeSpan.Zero, Backoff.Max);
    }

    [Fact]
    public void Retry_after_is_honoured_and_success_resets()
    {
        var backoff = new Backoff(new Random(1));
        Assert.Equal(TimeSpan.FromSeconds(10), backoff.NextDelay(retryAfter: TimeSpan.FromSeconds(10)));
        backoff.NextDelay();
        backoff.Reset();
        Assert.Equal(0, backoff.Failures);
        Assert.InRange(backoff.NextDelay(), TimeSpan.Zero, Backoff.Initial);
    }

    private sealed class MaxRandom : Random
    {
        public override double NextDouble() => 1.0;
    }
}
