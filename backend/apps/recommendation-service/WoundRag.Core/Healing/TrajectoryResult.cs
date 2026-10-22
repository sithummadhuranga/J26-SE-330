// A case's healing trajectory: PAR and elapsed weeks when computable, otherwise why not.

namespace WoundRag.Core.Healing;

public sealed record TrajectoryResult(bool Computable, string? NotComputableReason, double? Par, double? ElapsedWeeks, bool Stalled);
