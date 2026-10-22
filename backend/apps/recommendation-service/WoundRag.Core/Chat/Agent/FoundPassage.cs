// A passage the search returned.

namespace WoundRag.Core.Chat.Agent;

public sealed record FoundPassage(string ChunkId, string ChunkHash, string Text);
