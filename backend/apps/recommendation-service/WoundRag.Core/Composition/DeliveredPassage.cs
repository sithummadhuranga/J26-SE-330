// A passage as delivered: its tag (S1, S2, ...) and the section it is placed in.

namespace WoundRag.Core.Composition;

public sealed record DeliveredPassage(string Tag, string Section, Passage Passage);
