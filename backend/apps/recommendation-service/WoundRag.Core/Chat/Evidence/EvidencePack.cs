// The passages a session may cite. It starts as the protocol's citations with the protocol's own tags (S1…Sn);
// a passage found later gets the next tag. Nothing is removed or renumbered, so earlier answers stay valid and
// the prompt's prefix stays stable for the provider's cache (demo decision 50).

namespace WoundRag.Core.Chat.Evidence;

// An append-only, tag-stable list of citable passages.
public sealed class EvidencePack
{
    private readonly List<EvidencePassage> _passages;

    public EvidencePack(IEnumerable<EvidencePassage> passages)
    {
        _passages = [.. passages];
        for (var i = 0; i < _passages.Count; i++)
        {
            if (_passages[i].Tag != $"S{i + 1}")
                throw new ArgumentException($"tag {_passages[i].Tag} at position {i + 1}: expected S{i + 1}", nameof(passages));
        }
        if (_passages.DistinctBy(p => p.ChunkId).Count() != _passages.Count)
            throw new ArgumentException("a chunk appears twice in the pack", nameof(passages));
    }

    public IReadOnlyList<EvidencePassage> All => _passages;

    public IReadOnlySet<string> Tags => _passages.Select(p => p.Tag).ToHashSet(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> TextByTag => _passages.ToDictionary(p => p.Tag, p => p.Text, StringComparer.Ordinal);

    public EvidencePassage AddOrGet(string chunkId, string chunkHash, string text)
    {
        var known = _passages.Find(p => p.ChunkId == chunkId);
        if (known is not null) return known;
        var added = new EvidencePassage($"S{_passages.Count + 1}", chunkId, chunkHash, text);
        _passages.Add(added);
        return added;
    }

    public IReadOnlyList<EvidencePassage> AddedSince(int count) => _passages.Skip(count).ToList();

    public EvidencePack Copy() => new(_passages);
}
