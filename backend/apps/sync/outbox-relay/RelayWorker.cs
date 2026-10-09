using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Npgsql;
using Sync.Common.Kafka;
using Sync.Common.Telemetry;

namespace OutboxRelay;

/// <summary>Publishes outbox rows to Kafka and marks them sent; SKIP LOCKED lets several relays run safely.</summary>
public sealed class RelayWorker(
    NpgsqlDataSource db, IProducer<string, byte[]> producer, ILogger<RelayWorker> logger) : BackgroundService
{
    public const int BatchSize = 100;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);

    public const string ClaimBatchSql = """
        SELECT outbox_id, topic, msg_key, payload::text, headers::text
        FROM messaging.outbox
        WHERE published_at IS NULL
        ORDER BY outbox_id
        LIMIT @batch
        FOR UPDATE SKIP LOCKED
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox relay polling messaging.outbox");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishBatchAsync(stoppingToken);
                if (published == 0) await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Nothing was marked published, so the same rows are retried on the next poll.
                logger.LogError(ex, "Outbox relay batch failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rows = new List<(long Id, string Topic, string Key, string Payload, string Headers)>();
        await using (var claim = new NpgsqlCommand(ClaimBatchSql, conn, tx))
        {
            claim.Parameters.AddWithValue("batch", BatchSize);
            await using var r = await claim.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        }
        if (rows.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        // Produce in order; acks=all + idempotence keep per-key ordering within the batch.
        var deliveries = rows.Select(row => producer.ProduceAsync(row.Topic, new Message<string, byte[]>
        {
            Key = row.Key,
            Value = Encoding.UTF8.GetBytes(row.Payload),
            Headers = ToKafkaHeaders(row.Headers, row.Id),
        }, ct)).ToList();
        await Task.WhenAll(deliveries);

        await using (var mark = new NpgsqlCommand(
            "UPDATE messaging.outbox SET published_at = now() WHERE outbox_id = ANY(@ids)", conn, tx))
        {
            mark.Parameters.AddWithValue("ids", rows.Select(r => r.Id).ToArray());
            await mark.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        foreach (var row in rows)
            SyncMetrics.OutboxPublished.Add(1, new KeyValuePair<string, object?>("topic", row.Topic));
        logger.LogInformation("Published {Count} outbox row(s)", rows.Count);
        return rows.Count;
    }

    private static Headers ToKafkaHeaders(string json, long outboxId)
    {
        var headers = new Headers { { "outbox-id", Encoding.UTF8.GetBytes(outboxId.ToString()) } };
        foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [])
            headers.Add(k, Encoding.UTF8.GetBytes(v));
        return headers;
    }
}
