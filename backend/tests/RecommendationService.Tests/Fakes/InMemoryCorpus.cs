// Hand-written in-memory stand-ins for the corpus, retrieval and rerank-score repositories: no database, and every
// call is recorded so a test can see what the service asked for.

using RecommendationService.Tests.Core;
using WoundRag.Core.Retrieval;
using WoundRag.Infrastructure;
using WoundRag.Infrastructure.Corpus;
using WoundRag.Infrastructure.Retrieval;

namespace RecommendationService.Tests.Fakes;

public sealed class InMemoryCorpus : ICorpusRepository, IGuidelineRetrieval, IRerankScoreRepository
{
    public const string Version = "test-1.r1";

    public static readonly CorpusInfo Info = new(Version, 20, false, false, "emb", 256, "rr", new string('b', 64));

    public static FrozenCorpus Frozen(params string[] templates) => new(Info, new string('c', 64), DateTimeOffset.UnixEpoch,
        TrajectoryTests.PadBps15, new SlotVocabulary(SlotVocabularyTests.R2Words.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value)),
        templates);

    // Stored queries, their search results by query text, and stored scores by query key.
    public List<StoredQuery> Queries { get; } = [];
    public Dictionary<string, QuerySearch> Searches { get; } = [];
    public Dictionary<string, Dictionary<string, double>> Scores { get; } = [];

    public List<(string Key, IReadOnlyCollection<string> ChunkIds)> ScoreRequests { get; } = [];

    // What LoadFrozenAsync returns; null stands for a version that is not stored or not frozen.
    public FrozenCorpus? Loaded { get; set; }
    public int Loads { get; private set; }

    public Task<FrozenCorpus?> LoadFrozenAsync(string version, CancellationToken ct)
    {
        Loads++;
        return Task.FromResult(Loaded?.Info.Version == version ? Loaded : null);
    }

    // Every stored query whose filter is known; the plan picks among them, as matching_queries.sql would allow.
    public Task<IReadOnlyList<StoredQuery>> MatchingQueriesAsync(string version, IReadOnlyDictionary<string, string> slots, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StoredQuery>>(Queries);

    public Task<QuerySearch> SearchAsync(CorpusInfo corpus, StoredQuery query, CancellationToken ct) => Task.FromResult(Searches[query.Text]);

    public Task<IReadOnlyDictionary<string, double>> ScoresAsync(string version, string queryKey, IReadOnlyCollection<string> chunkIds, CancellationToken ct)
    {
        ScoreRequests.Add((queryKey, chunkIds));
        var stored = Scores.GetValueOrDefault(queryKey) ?? [];
        return Task.FromResult<IReadOnlyDictionary<string, double>>(chunkIds.Where(stored.ContainsKey).ToDictionary(id => id, id => stored[id]));
    }
}
