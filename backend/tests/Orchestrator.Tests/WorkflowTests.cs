using System.Text.Json;
using Orchestrator.Clients;
using Orchestrator.Graph;
using Orchestrator.Persistence;
using Sync.Common.Contracts;
using Sync.Common.Recommendations;

namespace Orchestrator.Tests;

/// <summary>Every path through the §10.1 workflow ends with exactly one outcome, and only the success path stores.</summary>
public class WorkflowTests
{
    private static readonly RecommendationResponseValidator Validator = RecommendationResponseValidator.FromOutputDirectory();

    private static OrchestrationJob Job() =>
        new(new PersistedEvent(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid()), "00-trace-span-01");

    private static (WorkflowRunner Runner, FakeStore Store, FakeClient Client) Create(Func<RecommendationRequest, RecommendationCallResult>? answer = null)
    {
        var store = new FakeStore();
        var client = new FakeClient(answer ?? (r => new RecommendationCallResult(200, ValidBody(r), null)));
        return (new WorkflowRunner(new OrchestratorGraph(store, client, Validator)), store, client);
    }

    internal static JsonElement ValidBody(RecommendationRequest r, string tag = "S1") => JsonSerializer.SerializeToElement(new
    {
        contractVersion = "1.0",
        caseId = r.CaseId,
        revision = r.Revision,
        mode = "generated",
        retrievalMode = "hybrid",
        corpusVersion = "iwgdf-2023.r1",
        sections = new[] { new { heading = "Offloading", text = "Use a non-removable cast [S1].", citationTags = new[] { tag } } },
        citations = new[] { new { tag = "S1", chunkHash = "abc123", source = "IWGDF 2023" } },
        withheld = Array.Empty<object>(),
    });

    [Fact]
    public async Task Success_stores_once()
    {
        var (runner, store, client) = Create();
        var outcome = await runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(OutcomeKind.Stored, outcome.Kind);
        Assert.Equal(1, client.Calls);
        Assert.Single(store.Stored);
        Assert.Equal("iwgdf-2023.r1", store.Stored[0].CorpusVersion);
    }

    [Fact]
    public async Task Already_processed_stops_before_the_service_is_called()
    {
        var (runner, store, client) = Create();
        store.Processed = true;

        var outcome = await runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(OutcomeKind.AlreadyProcessed, outcome.Kind);
        Assert.Equal(0, client.Calls);
        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task Superseded_revision_gets_no_advice()
    {
        var (runner, store, client) = Create();
        store.Outdated = true;

        var outcome = await runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(OutcomeKind.Superseded, outcome.Kind);
        Assert.Equal(0, client.Calls);
        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task Missing_assessment_is_a_dead_letter()
    {
        var (runner, store, _) = Create();
        store.Missing = true;

        var outcome = await runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(OutcomeKind.DeadLetter, outcome.Kind);
        Assert.Equal("ASSESSMENT_NOT_FOUND", outcome.Detail);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(0)]     // timeout, unreachable or open circuit
    [InlineData(500)]
    public async Task Unavailable_service_is_deferred_for_retry(int status)
    {
        var (runner, store, _) = Create(_ => new RecommendationCallResult(status, null, "boom"));

        var outcome = await runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(OutcomeKind.Deferred, outcome.Kind);
        Assert.True(outcome.Retry);
        Assert.Empty(store.Stored);
    }

    [Theory]
    [InlineData(422)]
    [InlineData(409)]
    public async Task Contract_errors_go_straight_to_the_dlq(int status)
    {
        var (runner, store, _) = Create(_ => new RecommendationCallResult(status, null, null));

        var outcome = await runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(OutcomeKind.DeadLetter, outcome.Kind);
        Assert.False(outcome.Retry);
        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task Unresolved_citation_is_rejected_and_retried()
    {
        var (runner, store, _) = Create(r => new RecommendationCallResult(200, ValidBody(r, tag: "S9"), null));

        var outcome = await runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(OutcomeKind.Deferred, outcome.Kind);
        Assert.Contains("unresolved citation tags S9", outcome.Detail);
        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task Answer_for_another_case_is_rejected()
    {
        var (runner, store, _) = Create(r => new RecommendationCallResult(200,
            ValidBody(r with { CaseId = Guid.NewGuid() }), null));

        var outcome = await runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(OutcomeKind.Deferred, outcome.Kind);
        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task Body_breaking_the_schema_is_rejected()
    {
        var (runner, store, _) = Create(_ => new RecommendationCallResult(200,
            JsonSerializer.SerializeToElement(new { mode = "creative" }), null));

        var outcome = await runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(OutcomeKind.Deferred, outcome.Kind);
        Assert.StartsWith("INVALID_RESPONSE", outcome.Detail);
    }

    [Fact]
    public async Task Database_failure_is_a_failed_run_for_retry()
    {
        var (runner, store, _) = Create();
        store.ThrowOnBegin = true;

        var outcome = await runner.RunAsync(Job(), CancellationToken.None);

        Assert.Equal(OutcomeKind.Failed, outcome.Kind);
        Assert.True(outcome.Retry);
        Assert.Contains("database down", outcome.Detail);
    }

    [Fact]
    public async Task Redelivery_after_a_stored_result_does_not_store_twice()
    {
        var (runner, store, _) = Create();
        var job = Job();

        Assert.Equal(OutcomeKind.Stored, (await runner.RunAsync(job, CancellationToken.None)).Kind);
        Assert.Equal(OutcomeKind.AlreadyProcessed, (await runner.RunAsync(job, CancellationToken.None)).Kind);
        Assert.Single(store.Stored);
    }
}

internal sealed class FakeClient(Func<RecommendationRequest, RecommendationCallResult> answer) : IRecommendationClient
{
    public int Calls;

    public Task<RecommendationCallResult> RecommendAsync(RecommendationRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(answer(request));
    }
}

/// <summary>In-memory store that behaves like the inbox: once stored, the event counts as processed.</summary>
internal sealed class FakeStore : IOrchestratorStore
{
    public bool Processed, Outdated, Missing, ThrowOnBegin;
    public readonly List<RecommendationResponse> Stored = [];
    private readonly HashSet<Guid> _inbox = [];

    public Task<bool> BeginAsync(OrchestrationJob job, CancellationToken ct) => ThrowOnBegin
        ? throw new InvalidOperationException("database down")
        : Task.FromResult(Processed || _inbox.Contains(job.Event.EventId));

    public Task<bool> SupersedeIfOutdatedAsync(OrchestrationJob job, CancellationToken ct) => Task.FromResult(Outdated);

    public Task<AssessmentContext?> LoadContextAsync(PersistedEvent evt, CancellationToken ct) =>
        Task.FromResult(Missing ? null : MapperTests.Context(evt.AssessmentId, evt.Revision));

    public Task<bool> StoreRecommendationAsync(OrchestrationJob job, RecommendationResponse response, JsonElement rawBody,
        CancellationToken ct)
    {
        if (!_inbox.Add(job.Event.EventId)) return Task.FromResult(false);
        Stored.Add(response);
        return Task.FromResult(true);
    }

    public Task RecordDeferredAsync(PersistedEvent evt, CancellationToken ct) => Task.CompletedTask;
}
