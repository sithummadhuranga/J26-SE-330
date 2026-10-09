using System.Collections.Concurrent;
using Npgsql;

namespace SyncGateway.Auth;

/// <summary>Blocks tokens from revoked devices, with a short per-device cache to spare the database.</summary>
public sealed class DeviceRevocationCheck(NpgsqlDataSource db, TimeProvider clock)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (bool Allowed, DateTimeOffset CheckedAt)> _cache = new();

    /// <summary>False for a revoked device, and for one the identity service never registered.</summary>
    public async Task<bool> IsAllowedAsync(string deviceId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(deviceId, out var hit) && now - hit.CheckedAt < CacheFor) return hit.Allowed;

        await using var cmd = db.CreateCommand("SELECT revoked_at IS NULL FROM clinical.device WHERE device_id = @d");
        cmd.Parameters.AddWithValue("d", deviceId);
        var allowed = await cmd.ExecuteScalarAsync(ct) is true;
        _cache[deviceId] = (allowed, now);
        return allowed;
    }
}
