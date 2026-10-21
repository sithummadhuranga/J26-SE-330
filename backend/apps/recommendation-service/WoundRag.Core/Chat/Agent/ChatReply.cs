// A model's reply with its usage block. ReasoningTokens must stay 0: thinking is sent disabled.

using WoundRag.Core.Generation;

namespace WoundRag.Core.Chat.Agent;

public sealed record ChatReply(
    ReplyStatus Status,
    string? Text = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ReportedModel = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    int CachedTokens = 0,
    int ReasoningTokens = 0,
    int LatencyMs = 0,
    string? Detail = null,
    bool ReasoningReturned = false);
