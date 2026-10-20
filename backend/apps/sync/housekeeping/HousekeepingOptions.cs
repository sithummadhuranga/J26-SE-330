namespace Housekeeping;

/// <summary>How long things are kept (architecture §9.4). Set in the "Housekeeping" section of the configuration.</summary>
public sealed class HousekeepingOptions
{
    public const string Section = "Housekeeping";

    /// <summary>How often a cleanup cycle runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a published outbox row is kept for debugging before it's deleted. Unpublished rows are never deleted.</summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How old a change-log row has to be before it can be archived. This is well past pull's 60-second re-send window.</summary>
    public TimeSpan ChangeLogMargin { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How old an inbox row has to be before it's deleted. It's never shorter than the time Kafka could still redeliver it.</summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(8);

    public TimeSpan InboxSafetyMargin { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How many rows each DELETE handles, so no single statement holds locks for long.</summary>
    public int BatchSize { get; set; } = 5000;

    public static readonly TimeSpan MinChangeLogMargin = TimeSpan.FromMinutes(10);

    public void Validate()
    {
        if (Interval <= TimeSpan.Zero) throw new InvalidOperationException("Housekeeping:Interval must be positive.");
        if (OutboxRetention < TimeSpan.Zero) throw new InvalidOperationException("Housekeeping:OutboxRetention must not be negative.");
        if (ChangeLogMargin < MinChangeLogMargin)
            throw new InvalidOperationException(
                $"Housekeeping:ChangeLogMargin must be at least {MinChangeLogMargin} (pull re-sends the last 60 s, §7.2).");
        if (InboxRetention <= TimeSpan.Zero || InboxSafetyMargin < TimeSpan.Zero)
            throw new InvalidOperationException("Housekeeping:InboxRetention must be positive and InboxSafetyMargin not negative.");
        if (BatchSize is < 1 or > 100_000) throw new InvalidOperationException("Housekeeping:BatchSize must be 1-100000.");
    }
}
