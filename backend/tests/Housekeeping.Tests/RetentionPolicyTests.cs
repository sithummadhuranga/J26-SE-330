using Housekeeping;

namespace Housekeeping.Tests;

public class RetentionPolicyTests
{
    private static readonly long Week = (long)TimeSpan.FromDays(7).TotalMilliseconds;
    private static readonly HousekeepingOptions Defaults = new();

    [Fact]
    public void Inbox_rows_outlive_the_longest_topic_retention_plus_the_margin()
    {
        // Kafka's default 7 days + 1 day margin = 8 days, equal to the configured minimum.
        Assert.Equal(TimeSpan.FromDays(8), RetentionPolicy.InboxRetention(Defaults, [Week, Week / 7]));

        var thirtyDays = (long)TimeSpan.FromDays(30).TotalMilliseconds;
        Assert.Equal(TimeSpan.FromDays(31), RetentionPolicy.InboxRetention(Defaults, [Week, thirtyDays]));
    }

    [Fact]
    public void Configured_minimum_applies_when_topics_are_trimmed_sooner()
    {
        var oneHour = (long)TimeSpan.FromHours(1).TotalMilliseconds;
        Assert.Equal(TimeSpan.FromDays(8), RetentionPolicy.InboxRetention(Defaults, [oneHour]));
    }

    [Fact]
    public void A_topic_kept_forever_keeps_every_inbox_row()
    {
        Assert.Null(RetentionPolicy.InboxRetention(Defaults, [Week, -1]));
    }

    [Fact]
    public void Unknown_redelivery_window_keeps_every_inbox_row()
    {
        Assert.Null(RetentionPolicy.InboxRetention(Defaults, null));
        Assert.Null(RetentionPolicy.InboxRetention(Defaults, []));
    }

    [Fact]
    public void Defaults_are_valid()
    {
        new HousekeepingOptions().Validate();
    }

    [Fact]
    public void Change_log_margin_must_stay_far_above_the_pull_resend_window()
    {
        var o = new HousekeepingOptions { ChangeLogMargin = TimeSpan.FromSeconds(60) };
        Assert.Throws<InvalidOperationException>(o.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200_000)]
    public void Batch_size_is_bounded(int batch)
    {
        Assert.Throws<InvalidOperationException>(new HousekeepingOptions { BatchSize = batch }.Validate);
    }
}
