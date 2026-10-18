// The stored passages a response is composed from. An ID the version does not hold is simply absent.

using WoundRag.Core.Composition;

namespace WoundRag.Infrastructure.Composition;

public interface IPassageRepository
{
    Task<IReadOnlyDictionary<string, Passage>> PassagesAsync(string version, IReadOnlyCollection<string> chunkIds, CancellationToken ct);
}
