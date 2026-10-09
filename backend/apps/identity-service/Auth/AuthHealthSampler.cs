using Npgsql;

namespace IdentityService.Auth;

/// <summary>Samples session health once a minute for the auth metrics gauges.</summary>
public sealed class AuthHealthSampler(NpgsqlDataSource db, ILogger<AuthHealthSampler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public const string SnapshotSql = """
        WITH latest AS (
            SELECT DISTINCT ON (family_id) family_id, client_id, revoked_at, expires_at
            FROM clinical.clinician_session
            WHERE (revoked_at IS NULL AND expires_at > now() - interval '24 hours')
               OR revoked_at > now() - interval '24 hours'
            ORDER BY family_id, issued_at DESC
        ),
        families AS (
            SELECT l.*, f.started_at,
                   l.revoked_at IS NULL AND l.expires_at > now() AS active
            FROM latest l
            CROSS JOIN LATERAL (SELECT min(issued_at) AS started_at FROM clinical.clinician_session s
                                WHERE s.family_id = l.family_id) f
        )
        SELECT client_id,
               count(*) FILTER (WHERE active),
               avg(extract(epoch FROM now() - started_at)) FILTER (WHERE active),
               count(*) FILTER (WHERE revoked_at IS NOT NULL),
               avg(extract(epoch FROM revoked_at - started_at)) FILTER (WHERE revoked_at IS NOT NULL),
               count(*) FILTER (WHERE NOT active AND revoked_at IS NULL)
        FROM families
        GROUP BY client_id
        ORDER BY client_id
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AuthMetrics.InitializeSeries();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                AuthMetrics.Snapshot = await SampleAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not sample session health; keeping the previous values");
            }
            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<AuthHealthSnapshot> SampleAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(SnapshotSql);
        var clients = new List<ClientSessionHealth>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            clients.Add(new ClientSessionHealth(r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : (double)r.GetDecimal(2),
                r.GetInt64(3), r.IsDBNull(4) ? null : (double)r.GetDecimal(4), r.GetInt64(5)));
        return new AuthHealthSnapshot(clients);
    }
}
