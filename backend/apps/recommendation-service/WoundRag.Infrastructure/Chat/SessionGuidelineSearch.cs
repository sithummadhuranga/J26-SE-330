// The search tool's gated search for one session: the repository's SQL with the session's observed fields.

using WoundRag.Core.Chat.Agent;

namespace WoundRag.Infrastructure.Chat;

public sealed class SessionGuidelineSearch(IChatRepository repository, CorpusInfo corpus, IReadOnlyList<string> observed) : IGuidelineSearch
{
    public Task<IReadOnlyList<FoundPassage>> SearchAsync(string query, int limit, CancellationToken ct) =>
        repository.SearchAsync(corpus, observed, query, limit, ct);

    public Task<IReadOnlyList<WithheldMatch>> WithheldAsync(string query, int limit, CancellationToken ct) =>
        repository.WithheldAsync(corpus, observed, query, limit, ct);
}
