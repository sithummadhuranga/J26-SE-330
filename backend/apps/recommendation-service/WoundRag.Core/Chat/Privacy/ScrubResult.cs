// The scrubbed text and the kinds removed, each once, in PatientDetailScrubber.Kinds order.

namespace WoundRag.Core.Chat.Privacy;

public sealed record ScrubResult(string Text, IReadOnlyList<string> Removed);
