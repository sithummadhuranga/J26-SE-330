// One model's reply with what the audit records about it. Detail is a code such as "HTTP 429", never the key or
// the prompt. Thinking is sent off, so reasoning tokens or text coming back is a regression and is reported.

namespace WoundRag.Core.Generation;

public sealed record GeneratorReply(
    ReplyStatus Status,
    string? Text = null,
    string? ReportedModel = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    int ReasoningTokens = 0,
    int LatencyMs = 0,
    string? Detail = null,
    bool ReasoningReturned = false);
