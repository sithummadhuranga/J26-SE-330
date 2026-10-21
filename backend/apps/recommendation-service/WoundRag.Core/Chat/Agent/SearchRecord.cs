// One search, as audited: counts only, never passage text.

namespace WoundRag.Core.Chat.Agent;

public sealed record SearchRecord(string Query, int Found, int Added);
