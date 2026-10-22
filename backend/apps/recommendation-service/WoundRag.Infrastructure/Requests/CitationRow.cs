// One citation of a sent answer, with the hash of the text it cited (rag.protocol_citations).

namespace WoundRag.Infrastructure.Requests;

public sealed record CitationRow(string Tag, string ChunkId, string ChunkHash);
