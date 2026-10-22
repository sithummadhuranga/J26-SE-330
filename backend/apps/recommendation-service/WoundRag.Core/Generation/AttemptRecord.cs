// One generation attempt, numbered across the whole chain from 1.

namespace WoundRag.Core.Generation;

public sealed record AttemptRecord(int Attempt, string Provider, string RequestedModel, string Outcome, GeneratorReply Reply, string? ProvenanceProblem);
