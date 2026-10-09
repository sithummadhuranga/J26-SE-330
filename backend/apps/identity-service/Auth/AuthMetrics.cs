using System.Diagnostics.Metrics;
using Sync.Common.Telemetry;

namespace IdentityService.Auth;

/// <summary>Auth health metrics: login failures, lockouts and session lifetimes for Prometheus.</summary>
public static class AuthMetrics
{
    private static readonly Meter Meter = new(SyncMetrics.MeterName);

    /// <summary>One per audit.auth_audit row, with the same action, success and reason code.</summary>
    public static readonly Counter<long> Events =
        Meter.CreateCounter<long>("auth.events", "{event}", "Auth audit events by action, success and reason");

    private static volatile AuthHealthSnapshot _snapshot = AuthHealthSnapshot.Empty;

    /// <summary>Replaced by <see cref="AuthHealthSampler"/> once a minute; the gauges below read the latest.</summary>
    public static AuthHealthSnapshot Snapshot
    {
        get => _snapshot;
        set => _snapshot = value;
    }

    static AuthMetrics()
    {
        Meter.CreateObservableGauge("auth.sessions.active",
            () => Snapshot.Clients.Select(c => new Measurement<long>(c.Active, Tag("client", c.Client))),
            "{session}", "Logins whose session is still valid");
        Meter.CreateObservableGauge("auth.session.lifetime",
            () => Snapshot.Clients.SelectMany(c => new[]
            {
                c.ActiveAgeSeconds is { } a ? new Measurement<double>(a, Tag("client", c.Client), Tag("state", "active")) : (Measurement<double>?)null,
                c.EndedLifetimeSeconds is { } e ? new Measurement<double>(e, Tag("client", c.Client), Tag("state", "ended")) : null,
            }).OfType<Measurement<double>>(),
            "s", "Average session lifetime: age of active sessions, login to logout/revocation of sessions ended in 24 h");
        Meter.CreateObservableGauge("auth.sessions.ended",
            () => Snapshot.Clients.SelectMany(c => new[]
            {
                new Measurement<long>(c.Ended, Tag("client", c.Client), Tag("how", "revoked")),
                new Measurement<long>(c.Expired, Tag("client", c.Client), Tag("how", "expired")),
            }),
            "{session}", "Sessions that ended in the last 24 h: logout or revocation, or refresh token expired unused");
    }

    /// <summary>Starts each series at 0 so Prometheus' increase() doesn't miss the first event after a restart.</summary>
    public static void InitializeSeries()
    {
        foreach (var (action, success, reason) in new (string, bool, string?)[]
                 {
                     ("LOGIN", true, null), ("LOGIN", false, "INVALID_CREDENTIALS"), ("LOGIN", false, "INVALID_TOTP"),
                     ("LOGIN", false, "CREDENTIAL_LOCKED"), ("LOGIN", false, "MFA_REQUIRED"),
                     ("LOGIN", false, "DEVICE_NOT_ALLOWED"), ("LOGIN", false, "CLIENT_NOT_ALLOWED"),
                     ("LOCKOUT", false, "CREDENTIAL_LOCKED"),
                 })
            Events.Add(0, Tag("action", action), Tag("success", success ? "true" : "false"), Tag("reason", reason ?? "none"));
    }

    public static void Record(string action, bool success, string? reason) =>
        Events.Add(1, Tag("action", action), Tag("success", success ? "true" : "false"), Tag("reason", reason ?? "none"));

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}

public sealed record ClientSessionHealth(string Client, long Active, double? ActiveAgeSeconds, long Ended,
    double? EndedLifetimeSeconds, long Expired);

public sealed record AuthHealthSnapshot(IReadOnlyList<ClientSessionHealth> Clients)
{
    public static readonly AuthHealthSnapshot Empty = new([]);
}
