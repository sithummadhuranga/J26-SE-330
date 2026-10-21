// One model call of a turn, as the audit records it.

namespace WoundRag.Core.Chat.Agent;

public sealed record ModelCall(string Purpose, string Provider, string RequestedModel, ChatReply Reply);
