// The corpus repository against the frozen iwgdf-2023.r2: what it reads must be what the Core tests assume (the
// stalled-healing rule, the slot words) and what the committed manifest records. Reads settings, IDs and words only.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Npgsql;
using RecommendationService.Tests.Core;
using WoundRag.Core.Cases;
using WoundRag.Core.Gating;
using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;
using WoundRag.Infrastructure;
using WoundRag.Infrastructure.Corpus;

namespace RecommendationService.Tests.Infrastructure;

public sealed class PostgresCorpusRepositoryTests : IDisposable
{
    private readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(TestDatabase.RagConnectionString() ?? "Host=unused");

    private PostgresCorpusRepository Repository => new(_db);

    public void Dispose() => _db.Dispose();

    private static readonly string ManifestPath = Path.Combine(AppContext.BaseDirectory, "manifests", "iwgdf-2023.r2.json");

    [DatabaseFact]
    public async Task The_frozen_version_is_the_one_the_committed_manifest_records()
    {
        var corpus = (await Repository.LoadFrozenAsync(TestDatabase.CorpusVersion, CancellationToken.None))!;
        var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))!;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ManifestPath))), corpus.ManifestSha256);
        Assert.Equal(DateTimeOffset.Parse((string)manifest["frozen_at"]!), corpus.FrozenAt);
        Assert.Equal(((int)manifest["retrieval"]!["k"]!, (string)manifest["reranker"]!["model"]!), (corpus.Info.RetrievalK, corpus.Info.RerankerModel));
        Assert.Equal(["RECOMMENDATION", "TABLE_ROW"], corpus.Info.RetrievalPool);
    }

    [DatabaseFact]
    public async Task The_stalled_healing_rule_comes_from_r2s_thresholds() =>
        Assert.Equal(TrajectoryTests.PadBps15, (await Repository.LoadFrozenAsync(TestDatabase.CorpusVersion, CancellationToken.None))!.StalledRule);

    [DatabaseFact]
    public async Task The_slot_words_are_the_ones_the_core_tests_assume()
    {
        var corpus = (await Repository.LoadFrozenAsync(TestDatabase.CorpusVersion, CancellationToken.None))!;
        foreach (var (slot, words) in SlotVocabularyTests.R2Words)
            Assert.Equal(words.Order(StringComparer.Ordinal), corpus.Vocabulary.KnownValues(slot).Order(StringComparer.Ordinal));
        Assert.Equal(["T1", "T2", "T3", "T4", "T5"], corpus.Templates);
    }

    [DatabaseFact]
    public async Task A_version_that_is_not_stored_is_not_served() =>
        Assert.Null(await Repository.LoadFrozenAsync("iwgdf-2023.r0", CancellationToken.None));

    [DatabaseFact]
    public async Task A_case_with_nothing_recorded_runs_one_default_query_per_template()
    {
        var corpus = (await Repository.LoadFrozenAsync(TestDatabase.CorpusVersion, CancellationToken.None))!;
        var slots = QuerySlots.For(ClinicalAssessmentTests.NothingRecorded(), new TrajectoryResult(false, "no earlier measurement", null, null, false));
        var plan = QueryPlan.Choose(corpus.Templates, await Repository.MatchingQueriesAsync(corpus.Info.Version, slots, CancellationToken.None), stalled: false);
        Assert.Equal(corpus.Templates, plan.Select(q => q.TemplateId));
        Assert.All(plan, q => Assert.Equal(QueryFilters.Default, q.Filter));
    }

    [DatabaseFact]
    public async Task Stalled_healing_with_absent_pulses_escalates_the_vascular_templates()
    {
        var corpus = (await Repository.LoadFrozenAsync(TestDatabase.CorpusVersion, CancellationToken.None))!;
        var clinical = new ClinicalAssessment("plantar_forefoot", "absent", "absent", "present", "mild", null);
        var slots = QuerySlots.For(clinical, new TrajectoryResult(true, null, 20, 6, Stalled: true));
        var plan = QueryPlan.Choose(corpus.Templates, await Repository.MatchingQueriesAsync(corpus.Info.Version, slots, CancellationToken.None), stalled: true);
        Assert.Equal(["T3", "T5"], plan.Where(q => q.Filter == QueryFilters.VascularEscalation).Select(q => q.TemplateId));
    }

    [DatabaseFact]
    public async Task A_word_the_corpus_lacks_matches_no_stored_query_which_is_why_it_is_refused_first()
    {
        var corpus = (await Repository.LoadFrozenAsync(TestDatabase.CorpusVersion, CancellationToken.None))!;
        var clinical = new ClinicalAssessment("heel", GateFields.NotRecorded, GateFields.NotRecorded, GateFields.NotRecorded, GateFields.NotRecorded, null);
        var slots = QuerySlots.For(clinical, new TrajectoryResult(false, "no earlier measurement", null, null, false));
        Assert.Equal([GateFields.UlcerLocation], corpus.Vocabulary.UnknownFields(slots));
        Assert.DoesNotContain("T1", (await Repository.MatchingQueriesAsync(corpus.Info.Version, slots, CancellationToken.None)).Select(q => q.TemplateId));
    }

    [Fact]
    public void Core_and_the_phase_a_retriever_name_the_filters_alike() =>
        Assert.Equal((RetrievalFilters.Default, RetrievalFilters.VascularEscalation), (QueryFilters.Default, QueryFilters.VascularEscalation));
}
