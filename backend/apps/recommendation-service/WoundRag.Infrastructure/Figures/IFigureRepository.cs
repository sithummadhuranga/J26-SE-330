// The stored figures that delivered passages cross-reference. Links come only from rag.figure_links (explicit
// references in the guideline text), never from caption similarity (SKILL.md §5).

using WoundRag.Core.Composition;

namespace WoundRag.Infrastructure.Figures;

public interface IFigureRepository
{
    Task<IReadOnlyList<LinkedFigure>> LinkedAsync(string version, IReadOnlyCollection<string> chunkIds, CancellationToken ct);
}
