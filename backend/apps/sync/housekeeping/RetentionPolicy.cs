namespace Housekeeping;

/// <summary>The retention decisions on their own, kept separate from the SQL so they can be unit-tested.</summary>
public static class RetentionPolicy
{
    /// <summary>How old an inbox row must be before it's deleted, based on each topic's retention.ms. Null means keep everything.</summary>
    public static TimeSpan? InboxRetention(HousekeepingOptions options, IReadOnlyCollection<long>? topicRetentions)
    {
        // Without Kafka's answer we don't know the redelivery window, so keep everything rather than guess.
        if (topicRetentions is null || topicRetentions.Count == 0) return null;
        // A topic that's never trimmed could redeliver anything, so we still need every inbox row.
        if (topicRetentions.Any(ms => ms < 0)) return null;

        var redelivery = TimeSpan.FromMilliseconds(topicRetentions.Max()) + options.InboxSafetyMargin;
        return redelivery > options.InboxRetention ? redelivery : options.InboxRetention;
    }
}
