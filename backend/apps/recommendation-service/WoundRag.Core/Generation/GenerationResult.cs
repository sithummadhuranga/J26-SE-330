// The chain's decision: the mode, the accepted text (generated only), every attempt, and whether a model refused
// the request, which is a configuration fault someone must fix.

namespace WoundRag.Core.Generation;

public sealed record GenerationResult(string Mode, string? Text, IReadOnlyList<AttemptRecord> Attempts, bool Alert);
