// A passage as the model sees it: its tag (S1, S2, ...) and its stored chunk text.

namespace WoundRag.Core.Generation;

public sealed record TaggedPassage(string Tag, string Text);
