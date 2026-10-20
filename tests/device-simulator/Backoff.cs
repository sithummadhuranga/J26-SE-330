namespace DeviceSimulator;

/// <summary>How long to wait before retrying. It starts at 2 seconds and doubles up to 5 minutes, with random jitter,
/// and resets after a success. If the server's Retry-After is longer, we use that instead.</summary>
public sealed class Backoff(Random random)
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    private int _failures;

    public int Failures => _failures;

    public void Reset() => _failures = 0;

    /// <summary>Notes a failure and returns how long to wait before trying again.</summary>
    public TimeSpan NextDelay(TimeSpan? retryAfter = null)
    {
        var ceiling = TimeSpan.FromTicks(Math.Min(Max.Ticks, Initial.Ticks * (1L << Math.Min(_failures, 20))));
        _failures++;
        var jittered = TimeSpan.FromTicks((long)(random.NextDouble() * ceiling.Ticks)); // full jitter
        return retryAfter is { } ra && ra > jittered ? ra : jittered;
    }
}
