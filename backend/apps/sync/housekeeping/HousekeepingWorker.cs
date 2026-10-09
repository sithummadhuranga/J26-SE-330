using Microsoft.Extensions.Options;
using Npgsql;
using Sync.Common.Telemetry;

namespace Housekeeping;

public sealed record CycleResult(bool Ran, int OutboxDeleted, int ChangeLogArchived, int InboxDeleted,
    TimeSpan? InboxRetention, IReadOnlyList<HeldBackFacility> HeldBack);

/// <summary>Periodically cleans up outbox rows, archives old change-log rows and trims the inbox, in batches.</summary>
public sealed class HousekeepingWorker(
    NpgsqlDataSource db, KafkaRetentionProbe kafka, IOptions<HousekeepingOptions> options,
    ILogger<HousekeepingWorker> logger) : BackgroundService
{
    private readonly HousekeepingOptions _o = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Housekeeping cycle failed; retrying after the interval");
            }
            try
            {
                await Task.Delay(_o.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<CycleResult> RunCycleAsync(CancellationToken ct)
    {
        // Asked before taking the lock, so a slow Kafka never holds it.
        var inboxRetention = RetentionPolicy.InboxRetention(_o, await kafka.TopicRetentionsAsync());

        await using var conn = await db.OpenConnectionAsync(ct);
        if (!await HousekeepingStore.TryLockAsync(conn, ct))
        {
            logger.LogInformation("Another housekeeping instance is running a cycle; skipping");
            return new CycleResult(false, 0, 0, 0, inboxRetention, []);
        }

        try
        {
            var outbox = await DrainAsync(conn, HousekeepingStore.DeleteOutboxSql, "retention", _o.OutboxRetention, ct);
            var archived = await DrainAsync(conn, HousekeepingStore.ArchiveChangeLogSql, "margin", _o.ChangeLogMargin, ct);
            var inbox = inboxRetention is { } r
                ? await DrainAsync(conn, HousekeepingStore.DeleteInboxSql, "retention", r, ct)
                : 0;
            var heldBack = await HousekeepingStore.HeldBackAsync(conn, _o.ChangeLogMargin, ct);

            SyncMetrics.HousekeepingRows.Add(outbox, new KeyValuePair<string, object?>("table", "outbox"));
            SyncMetrics.HousekeepingRows.Add(archived, new KeyValuePair<string, object?>("table", "change_log"));
            SyncMetrics.HousekeepingRows.Add(inbox, new KeyValuePair<string, object?>("table", "inbox"));

            logger.LogInformation(
                "Housekeeping: {Outbox} outbox rows deleted, {Archived} change-log rows archived, {Inbox} inbox rows deleted (inbox retention {InboxRetention})",
                outbox, archived, inbox, inboxRetention?.ToString() ?? "keep all");
            foreach (var h in heldBack)
                logger.LogInformation(
                    "Change-log archival for {Facility} is held back by device {Device} at cursor {Cursor} (last pull {LastPull}); revoke it if the phone is lost",
                    h.FacilityId, h.DeviceId, h.DeviceCursor, h.LastPullAt?.ToString("u") ?? "never");

            return new CycleResult(true, outbox, archived, inbox, inboxRetention, heldBack);
        }
        finally
        {
            await HousekeepingStore.UnlockAsync(conn);
        }
    }

    private async Task<int> DrainAsync(NpgsqlConnection conn, string sql, string ageParameter, TimeSpan age,
        CancellationToken ct)
    {
        var total = 0;
        int n;
        do
        {
            n = await HousekeepingStore.ExecBatchAsync(conn, sql, ageParameter, age, _o.BatchSize, ct);
            total += n;
        } while (n == _o.BatchSize && !ct.IsCancellationRequested);
        return total;
    }
}
