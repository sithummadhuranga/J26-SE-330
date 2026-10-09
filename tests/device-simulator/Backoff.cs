namespace DeviceSimulator;

/// <summary>Retry delay: 2 s doubling to 5 min with full jitter, reset on success; longer Retry-After wins.</summary>
public sealed class Backoff(Random random)
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Max = TimeSpan.FromMinutes(5);

    private int _failures;

    public int Failures => _failures;

    public void Reset() => _failures = 0;

    /// <summary>Records a failure and returns how long to wait before the next try.</summary>
    public TimeSpan NextDelay(TimeSpan? retryAfter = null)
    {
        var ceiling = TimeSpan.FromTicks(Math.Min(Max.Ticks, Initial.Ticks * (1L << Math.Min(_failures, 20))));
        _failures++;
        var jittered = TimeSpan.FromTicks((long)(random.NextDouble() * ceiling.Ticks)); // full jitter
        return retryAfter is { } ra && ra > jittered ? ra : jittered;
    }
}
