// All of the chat's database access, so the chat use cases can be tested without a database.

using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Agent;

namespace WoundRag.Infrastructure.Chat;

public interface IChatRepository
{
    // The session of the latest answer for this caseId and revision, started now or found.
    Task<ChatStart> StartAsync(Guid caseId, int revision, string corpusVersion, DateTimeOffset now, CancellationToken ct);

    Task<ChatSession?> LoadAsync(Guid sessionId, CancellationToken ct);

    // Passages of the retrieval pool that match the query and whose Req(d) is within the observed fields.
    Task<IReadOnlyList<FoundPassage>> SearchAsync(CorpusInfo corpus, IReadOnlyList<string> observed, string query, int limit, CancellationToken ct);

    // The gate's withheld passages among the query's best matches in the whole pool, with what each is missing.
    Task<IReadOnlyList<WithheldMatch>> WithheldAsync(CorpusInfo corpus, IReadOnlyList<string> observed, string query, int limit, CancellationToken ct);

    // The turn, its new evidence and its audit in one transaction; false when the turn number was already taken.
    Task<bool> SaveTurnAsync(ChatSession session, int number, ChatTurnResult turn, DateTimeOffset now, CancellationToken ct);
}
