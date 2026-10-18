// One case, once its request has passed contract 1.0: what the trajectory, the gate and the query slots read.

namespace WoundRag.Core.Cases;

public sealed record CaseInput(Guid CaseId, int Revision, DateTimeOffset ReceivedAt, Measurement Current,
    IReadOnlyList<Measurement> History, ClinicalAssessment Clinical);
