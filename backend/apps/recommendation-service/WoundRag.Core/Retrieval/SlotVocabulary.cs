// The words the corpus's stored queries know per slot; a value outside them is named for a 422 (decision 71).

namespace WoundRag.Core.Retrieval;

public sealed class SlotVocabulary(IReadOnlyDictionary<string, IReadOnlyCollection<string>> known)
{
    public IReadOnlyList<string> UnknownFields(IReadOnlyDictionary<string, string> slots) =>
        [.. QuerySlots.Names.Where(n => slots.TryGetValue(n, out var value) && !KnownValues(n).Contains(value))];

    public IReadOnlyCollection<string> KnownValues(string slot) => known.TryGetValue(slot, out var words) ? words : [];
}
