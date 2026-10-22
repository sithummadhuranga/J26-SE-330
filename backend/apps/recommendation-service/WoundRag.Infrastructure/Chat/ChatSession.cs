// A loaded chat session: the answer it started from, what the chat may use from it, its evidence and its turns.

using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Evidence;

namespace WoundRag.Infrastructure.Chat;

public sealed record ChatSession(Guid SessionId, Guid RequestId, Guid CaseId, int Revision, string CorpusVersion, string Findings,
    IReadOnlyList<string> Observed, IReadOnlyList<string> Withheld, IReadOnlyList<SessionPassage> Evidence, IReadOnlyList<StoredTurn> Turns)
{
    public EvidencePack Pack() => new(Evidence.Select(e => e.Passage));

    public IReadOnlyList<PastTurn> History() => [.. Turns.Select(t => new PastTurn(t.Question, t.Outcome, t.English))];
}
