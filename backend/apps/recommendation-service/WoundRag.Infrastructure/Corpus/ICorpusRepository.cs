// What the service reads from the frozen corpus before it retrieves anything for a case.

using WoundRag.Core.Retrieval;

namespace WoundRag.Infrastructure.Corpus;

public interface ICorpusRepository
{
    // Null when the version is not stored or not frozen: only a frozen version is ever served.
    Task<FrozenCorpus?> LoadFrozenAsync(string version, CancellationToken ct);

    Task<IReadOnlyList<StoredQuery>> MatchingQueriesAsync(string version, IReadOnlyDictionary<string, string> slots, CancellationToken ct);
}
