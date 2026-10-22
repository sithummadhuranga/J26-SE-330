// An in-memory request store with the same rules as the database (decision 72): one stable hash per key, one answer
// per body. It counts calls, can fail like the database, and can pretend another request won a race.

using System.Data.Common;
using WoundRag.Infrastructure.Requests;

namespace RecommendationService.Tests.Fakes;

public sealed class InMemoryRequests : IRequestRepository
{
    public Dictionary<(Guid CaseId, int Revision), string> Keys { get; } = [];
    public Dictionary<(Guid CaseId, int Revision, string BodySha256), byte[]> Answers { get; } = [];
    public List<NewAnswer> Saved { get; } = [];
    public int Calls { get; private set; }

    public DbException? Failure { get; set; }
    public SaveOutcome? RaceOutcome { get; set; }

    public Task<RequestLookup?> FindAsync(Guid caseId, int revision, string bodySha256, CancellationToken ct)
    {
        Touch();
        return Task.FromResult(Keys.TryGetValue((caseId, revision), out var stable)
            ? new RequestLookup(stable, Answers.GetValueOrDefault((caseId, revision, bodySha256)))
            : null);
    }

    public Task<SaveOutcome> SaveAsync(NewAnswer answer, CancellationToken ct)
    {
        Touch();
        if (RaceOutcome is { } race) return Task.FromResult(race);
        if (Keys.TryGetValue((answer.CaseId, answer.Revision), out var stable) && stable != answer.StableSha256)
            return Task.FromResult(new SaveOutcome(SaveStatus.Conflict, null));
        if (Answers.TryGetValue((answer.CaseId, answer.Revision, answer.BodySha256), out var stored))
            return Task.FromResult(new SaveOutcome(SaveStatus.AlreadyAnswered, stored));
        Keys[(answer.CaseId, answer.Revision)] = answer.StableSha256;
        Answers[(answer.CaseId, answer.Revision, answer.BodySha256)] = answer.ResponseBody;
        Saved.Add(answer);
        return Task.FromResult(new SaveOutcome(SaveStatus.Saved, null));
    }

    private void Touch()
    {
        Calls++;
        if (Failure is not null) throw Failure;
    }
}

// A database failure whose transience the test chooses, as Npgsql reports it through DbException.IsTransient.
public sealed class FakeDbException(bool transient) : DbException("simulated database failure")
{
    public override bool IsTransient => transient;
}
