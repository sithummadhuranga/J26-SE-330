// An in-memory chat repository: answers are added by the test, sessions and turns are kept as the SQL would keep
// them (one session per answer, a turn number taken once), and every search is recorded.

using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Chat.Evidence;
using WoundRag.Infrastructure;
using WoundRag.Infrastructure.Chat;

namespace RecommendationService.Tests.Fakes;

public sealed class InMemoryChatRepository : IChatRepository
{
    public sealed record Answer(Guid RequestId, Guid CaseId, int Revision, string CorpusVersion, string Findings, string[] Observed,
        string[] Withheld, IReadOnlyList<SessionPassage> Citations);

    public List<Answer> Answers { get; } = [];
    public Dictionary<Guid, ChatSession> Sessions { get; } = [];
    public List<FoundPassage> SearchResults { get; } = [];
    public List<(string Query, IReadOnlyList<string> Observed)> Searches { get; } = [];
    public List<(int Number, ChatTurnResult Turn)> SavedTurns { get; } = [];

    // Set to make the next save find its turn number already taken, as a concurrent question would.
    public bool NextTurnTaken { get; set; }

    public Task<ChatStart> StartAsync(Guid caseId, int revision, string corpusVersion, DateTimeOffset now, CancellationToken ct)
    {
        var answer = Answers.LastOrDefault(a => a.CaseId == caseId && a.Revision == revision);
        if (answer is null) return Task.FromResult(new ChatStart(ChatStartKind.NoAnswer, null));
        if (answer.CorpusVersion != corpusVersion) return Task.FromResult(new ChatStart(ChatStartKind.CorpusDiffers, null));
        var existing = Sessions.Values.FirstOrDefault(s => s.RequestId == answer.RequestId);
        if (existing is not null) return Task.FromResult(new ChatStart(ChatStartKind.Existing, existing.SessionId));

        var session = new ChatSession(Guid.NewGuid(), answer.RequestId, caseId, revision, corpusVersion, answer.Findings, answer.Observed,
            answer.Withheld, answer.Citations, []);
        Sessions[session.SessionId] = session;
        return Task.FromResult(new ChatStart(ChatStartKind.Started, session.SessionId));
    }

    public Task<ChatSession?> LoadAsync(Guid sessionId, CancellationToken ct) => Task.FromResult(Sessions.GetValueOrDefault(sessionId));

    public Task<IReadOnlyList<FoundPassage>> SearchAsync(CorpusInfo corpus, IReadOnlyList<string> observed, string query, int limit, CancellationToken ct)
    {
        Searches.Add((query, observed));
        return Task.FromResult<IReadOnlyList<FoundPassage>>([.. SearchResults.Take(limit)]);
    }

    public Dictionary<string, List<WithheldMatch>> Withheld { get; } = [];

    public Task<IReadOnlyList<WithheldMatch>> WithheldAsync(CorpusInfo corpus, IReadOnlyList<string> observed, string query, int limit,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WithheldMatch>>(Withheld.GetValueOrDefault(query) ?? []);

    public Task<bool> SaveTurnAsync(ChatSession session, int number, ChatTurnResult turn, DateTimeOffset now, CancellationToken ct)
    {
        if (NextTurnTaken || session.Turns.Any(t => t.Number == number)) return Task.FromResult(false);
        SavedTurns.Add((number, turn));
        var stored = new StoredTurn(number, turn.Scrub.Text, turn.Scrub.Removed, turn.Language, turn.Outcome, turn.English, turn.Shown,
            turn.ShownLanguage, turn.Message, turn.Notices, turn.AnsweredBy, now);
        var added = turn.NewEvidence.Select(e => new SessionPassage(e, "PAD", 1, null, null));
        Sessions[session.SessionId] = session with { Turns = [.. session.Turns, stored], Evidence = [.. session.Evidence, .. added] };
        return Task.FromResult(true);
    }

    public static SessionPassage Passage(string tag, string chunkId) =>
        new(new EvidencePassage(tag, chunkId, new string('a', 64), $"passage {chunkId}"), "PAD", 12, "1", "Strong; Moderate");
}
