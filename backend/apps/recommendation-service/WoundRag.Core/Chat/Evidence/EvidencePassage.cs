// A citable passage with its stored chunk_hash.

namespace WoundRag.Core.Chat.Evidence;

public sealed record EvidencePassage(string Tag, string ChunkId, string ChunkHash, string Text);
