namespace Sync.Common.Kafka;

/// <summary>Retry chain: retry.30s → retry.5m → DLQ; contract errors skip straight to the DLQ.</summary>
public static class RetryRouting
{
    public const string RetryCountHeader = "retry-count";
    public const string OriginalTopicHeader = "original-topic";
    public const string ErrorHeader = "last-error";

    /// <summary>Where a message that failed while being read from <paramref name="currentTopic"/> goes next.</summary>
    public static string NextHop(string currentTopic) => currentTopic switch
    {
        Topics.Retry30s => Topics.Retry5m,
        Topics.Retry5m => Topics.DeadLetter,
        Topics.DeadLetter => Topics.DeadLetter,
        _ => Topics.Retry30s,
    };

    /// <summary>How long a retry consumer waits after the message timestamp before processing it.</summary>
    public static TimeSpan DelayFor(string retryTopic) => retryTopic switch
    {
        Topics.Retry30s => TimeSpan.FromSeconds(30),
        Topics.Retry5m => TimeSpan.FromMinutes(5),
        _ => TimeSpan.Zero,
    };
}
