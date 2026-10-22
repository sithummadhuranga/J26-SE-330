// A passage that matched a search but that the gate withholds for this case, with the fields it is waiting for.
// Only counted and named in a notice written by code; the model never sees it.

namespace WoundRag.Core.Chat.Agent;

public sealed record WithheldMatch(string ChunkId, IReadOnlyList<string> MissingFields);
