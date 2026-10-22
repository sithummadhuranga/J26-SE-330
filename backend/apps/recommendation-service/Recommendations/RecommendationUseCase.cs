// POST /v1/recommendations as a use case: a status and a body, so every status rule is tested without a web host.
// 422 before any database access; 503 when the corpus or the database is unavailable (her client retries later);
// 409 or a replay by decision 72; otherwise answered, checked against 1.0 and stored in one transaction.

using System.Data.Common;
using System.Text;
using RecommendationService.Configuration;
using RecommendationService.Contract;
using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;
using WoundRag.Infrastructure.Corpus;
using WoundRag.Infrastructure.Requests;

namespace RecommendationService.Recommendations;

public sealed class RecommendationUseCase(RequestReader reader, ResponseCheck check, ICorpusRepository corpus, CaseRetrievalService retrieval,
    ProtocolService protocol, IRequestRepository requests, RecommendationSettings settings, TimeProvider clock)
{
    public async Task<RecommendationResult> HandleAsync(string body, CancellationToken ct)
    {
        var read = reader.Read(body);
        if (read.Problems.Count > 0) return new(422, ErrorBody.ValidationFailed(read.Problems));
        var input = read.Case!;
        try
        {
            var frozen = await corpus.LoadFrozenAsync(settings.CorpusVersion, ct);
            if (frozen is null) return new(503, ErrorBody.Unavailable($"Corpus {settings.CorpusVersion} is not stored or not frozen."));

            // Decision 71: a word the corpus's queries do not hold is named, never guessed and never a retried 5xx.
            var trajectory = Trajectory.Evaluate(input.Current, input.ReceivedAt, input.History, frozen.StalledRule);
            var unknown = frozen.Vocabulary.UnknownFields(QuerySlots.For(input.Clinical, trajectory));
            if (unknown.Count > 0)
                return new(422, ErrorBody.ValidationFailed(unknown.Select(f => new FieldProblem($"/clinicalAssessment/{f}",
                    $"not a value {settings.CorpusVersion} holds; known: {string.Join(", ", frozen.Vocabulary.KnownValues(f).Order(StringComparer.Ordinal))}"))));

            var fingerprint = RequestFingerprint.Of(body);
            var found = await requests.FindAsync(input.CaseId, input.Revision, fingerprint.BodySha256, ct);
            if (found is not null && found.StableSha256 != fingerprint.StableSha256) return new(409, ErrorBody.Conflict());
            if (found?.ResponseForBody is { } stored) return new(200, stored, Replayed: true);

            var receivedAt = clock.GetUtcNow();
            var started = clock.GetTimestamp();
            var outcome = await retrieval.RunAsync(frozen, input, ct);
            var retrievalMs = (int)clock.GetElapsedTime(started).TotalMilliseconds;
            started = clock.GetTimestamp();
            var composed = await protocol.ComposeAsync(frozen, input, outcome, ct);
            var composeMs = (int)clock.GetElapsedTime(started).TotalMilliseconds;

            // A stored body is replayed forever, so one that fails 1.0 is a bug to fix (500), never stored.
            if (check.Problem(composed.Body, input.CaseId, input.Revision) is { } problem)
                throw new InvalidOperationException($"the composed response fails contract 1.0: {problem}");
            var bytes = Encoding.UTF8.GetBytes(composed.Body.ToJsonString());

            var answer = AnswerRecord.From(input, fingerprint, frozen.Info.Version, outcome, composed, bytes,
                new AnswerTiming(receivedAt, clock.GetUtcNow(), retrievalMs, composeMs));
            var saved = await requests.SaveAsync(answer, ct);
            return saved.Status switch
            {
                SaveStatus.Saved => new(200, bytes, Alert: composed.Generation.Alert),
                SaveStatus.AlreadyAnswered => new(200, saved.StoredResponse!, Replayed: true),
                _ => new(409, ErrorBody.Conflict()),
            };
        }
        catch (DbException e) when (e.IsTransient)
        {
            // Unreachable or overloaded: her client retries a 5xx later. A statement the server refused is not transient
            // and propagates as a 500, so a bug never looks like an outage.
            return new(503, ErrorBody.Unavailable("The database is unavailable; try again later."));
        }
    }
}
