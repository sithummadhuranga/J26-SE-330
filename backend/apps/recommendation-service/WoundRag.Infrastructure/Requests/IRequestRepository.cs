// The requests the service answered (decision 72). Find runs before any work so a retry is replayed cheaply; Save
// checks again inside its transaction, so two copies of one request racing each other still store one answer.

namespace WoundRag.Infrastructure.Requests;

public interface IRequestRepository
{
    Task<RequestLookup?> FindAsync(Guid caseId, int revision, string bodySha256, CancellationToken ct);

    Task<SaveOutcome> SaveAsync(NewAnswer answer, CancellationToken ct);
}
