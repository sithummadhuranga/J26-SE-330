// In-memory passages and figure links, recording which chunk IDs the service asked figures for.

using WoundRag.Core.Composition;
using WoundRag.Infrastructure.Composition;
using WoundRag.Infrastructure.Figures;

namespace RecommendationService.Tests.Fakes;

public sealed class InMemoryProtocolStore : IPassageRepository, IFigureRepository
{
    public Dictionary<string, Passage> Passages { get; } = [];
    public List<LinkedFigure> Figures { get; } = [];
    public List<IReadOnlyCollection<string>> FigureRequests { get; } = [];

    public Task<IReadOnlyDictionary<string, Passage>> PassagesAsync(string version, IReadOnlyCollection<string> chunkIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, Passage>>(chunkIds.Where(Passages.ContainsKey).ToDictionary(id => id, id => Passages[id]));

    public Task<IReadOnlyList<LinkedFigure>> LinkedAsync(string version, IReadOnlyCollection<string> chunkIds, CancellationToken ct)
    {
        FigureRequests.Add(chunkIds);
        return Task.FromResult<IReadOnlyList<LinkedFigure>>([.. Figures.Where(f => f.LinkedChunkIds.Any(chunkIds.Contains))]);
    }
}
