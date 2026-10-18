// Everything one answered request writes, in one transaction: the key, the attempt, its retrievals, the body sent,
// its citations and its audit.

namespace WoundRag.Infrastructure.Requests;

public sealed record NewAnswer(Guid RequestId, Guid CaseId, int Revision, string StableSha256, string BodySha256, string CorpusVersion,
    DateTimeOffset ReceivedAt, DateTimeOffset AnsweredAt, IReadOnlyList<RetrievalRow> Retrievals, string Mode, string RetrievalMode, byte[] ResponseBody,
    IReadOnlyList<CitationRow> Citations, IReadOnlyList<AuditRow> Audit);
