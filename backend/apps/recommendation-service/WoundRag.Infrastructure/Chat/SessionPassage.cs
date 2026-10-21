// A passage of a session's evidence pack with what its citation shows. PdfIndex is the PDF page index, not the
// printed page number.

using WoundRag.Core.Chat.Evidence;

namespace WoundRag.Infrastructure.Chat;

public sealed record SessionPassage(EvidencePassage Passage, string SubGuideline, int PdfIndex, string? RecommendationId, string? Grade);
