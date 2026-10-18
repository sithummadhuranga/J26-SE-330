// The two endpoints through the real web host. A connection string pointing nowhere proves a 422 needs no database
// and an unreachable one is a 503; in-memory repositories stand in for the answered cases. Figures follow Member 4's
// gateway: ETag, 304, Cache-Control and attribution passed through, and no licence or tier header.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RecommendationService.Tests.Fakes;
using WoundRag.Core.Composition;
using WoundRag.Core.Gating;
using WoundRag.Core.Generation;
using WoundRag.Core.Retrieval;
using WoundRag.Infrastructure.Composition;
using WoundRag.Infrastructure.Corpus;
using WoundRag.Infrastructure.Figures;
using WoundRag.Infrastructure.Requests;
using WoundRag.Infrastructure.Retrieval;
using static RecommendationService.Tests.Core.CompositionFixtures;

namespace RecommendationService.Tests.Service;

public class EndpointTests
{
    private const string Nowhere = "Host=127.0.0.1;Port=1;Database=none;Username=rag_svc;Timeout=3";

    private sealed class Fakes
    {
        public InMemoryCorpus Corpus { get; } = new();
        public InMemoryProtocolStore Store { get; } = new();
        public InMemoryRequests Requests { get; } = new();

        public Fakes()
        {
            Corpus.Loaded = InMemoryCorpus.Frozen("T1") with { Info = InMemoryCorpus.Info with { Version = TestDatabase.CorpusVersion } };
            Corpus.Queries.Add(new StoredQuery("T1", "q1", QueryFilters.Default));
            Corpus.Searches["q1"] = new(Corpus.Queries[0], "k1", true,
                [new("A-R01", Hash("A-R01"), "RECOMMENDATION", [], 1), new("P-R01", Hash("P-R01"), "RECOMMENDATION", [GateFields.PedalPulses], 2)]);
            Corpus.Scores["k1"] = new() { ["A-R01"] = 0.9, ["P-R01"] = 0.5 };
            foreach (var p in new[] { Recommendation("A-R01"), Recommendation("P-R01") }) Store.Passages[p.ChunkId] = p;
            Store.Images[(TestDatabase.CorpusVersion, "PAD-F2")] = new FigureImage([1, 2, 3, 4], "image/png", new string('f', 64));
        }

        public void Register(IServiceCollection services)
        {
            services.AddSingleton<ICorpusRepository>(Corpus);
            services.AddSingleton<IGuidelineRetrieval>(Corpus);
            services.AddSingleton<IRerankScoreRepository>(Corpus);
            services.AddSingleton<IPassageRepository>(Store);
            services.AddSingleton<IFigureRepository>(Store);
            services.AddSingleton<IRequestRepository>(Requests);
            services.AddSingleton(new GenerationChain([new ScriptedWriter()]));
            services.AddSingleton(Placement(("A-R01", "management"), ("P-R01", "management")));
        }
    }

    private static WebApplicationFactory<Program> Host(Fakes? fakes = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", Nowhere);
            if (fakes is not null) b.ConfigureTestServices(fakes.Register);
        });

    private static Task<HttpResponseMessage> Post(HttpClient client, JsonObject body) =>
        client.PostAsync("/v1/recommendations", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

    [Fact]
    public async Task An_invalid_body_is_a_422_without_touching_the_database()
    {
        await using var host = Host();
        var body = RecommendationUseCaseTests.Request();
        body.Remove("revision");
        var response = await Post(host.CreateClient(), body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("validation_failed", (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!);
    }

    [Fact]
    public async Task A_valid_body_with_the_database_unreachable_is_a_503()
    {
        await using var host = Host();
        var response = await Post(host.CreateClient(), RecommendationUseCaseTests.Request());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("unavailable", (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!);
    }

    [Fact]
    public async Task An_answer_is_a_200_and_its_retry_replays_the_same_bytes_marked_as_replayed()
    {
        await using var host = Host(new Fakes());
        var client = host.CreateClient();
        var first = await Post(client, RecommendationUseCaseTests.Request());
        var again = await Post(client, RecommendationUseCaseTests.Request());

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, again.StatusCode));
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(["true"], again.Headers.GetValues("Idempotent-Replayed"));
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await again.Content.ReadAsByteArrayAsync());
        Assert.Null(TheirValidator.Check(JsonNode.Parse(await first.Content.ReadAsStringAsync())!.AsObject(),
            Guid.Parse("6f1d3c9e-8a2b-4c5d-9e0f-1a2b3c4d5e6f"), 2));
    }

    [Fact]
    public async Task A_different_request_under_the_same_key_is_a_409()
    {
        await using var host = Host(new Fakes());
        var client = host.CreateClient();
        await Post(client, RecommendationUseCaseTests.Request());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, RecommendationUseCaseTests.Request(pedalPulses: "absent"))).StatusCode);
    }

    [Fact]
    public async Task A_figure_is_served_with_its_etag_cache_rule_and_attribution_and_no_licence_or_tier()
    {
        await using var host = Host(new Fakes());
        var response = await host.CreateClient().GetAsync($"/v1/figures/{TestDatabase.CorpusVersion}/PAD-F2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{new string('f', 64)}\"", response.Headers.ETag!.Tag);
        var cache = response.Headers.CacheControl!;
        Assert.Equal((true, TimeSpan.FromSeconds(31536000)), (cache.Private, cache.MaxAge));
        Assert.Contains(cache.Extensions, e => e.Name == "immutable");
        Assert.Equal(["Copyright 2023 The International Working Group on the Diabetic Foot"], response.Headers.GetValues("X-Figure-Attribution"));
        Assert.False(response.Headers.Contains("X-Figure-Licence"));
        Assert.False(response.Headers.Contains("X-Figure-Tier"));
    }

    [Fact]
    public async Task A_figure_the_client_already_holds_is_a_304()
    {
        await using var host = Host(new Fakes());
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/figures/{TestDatabase.CorpusVersion}/PAD-F2");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{new string('f', 64)}\""));
        var response = await host.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal($"\"{new string('f', 64)}\"", response.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task An_unknown_figure_or_version_is_a_404()
    {
        await using var host = Host(new Fakes());
        var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1/figures/{TestDatabase.CorpusVersion}/PAD-F99")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/figures/iwgdf-2023.r1/PAD-F2")).StatusCode);
    }

    [Fact]
    public async Task A_figure_with_the_database_unreachable_is_a_503()
    {
        await using var host = Host();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.CreateClient().GetAsync($"/v1/figures/{TestDatabase.CorpusVersion}/PAD-F2")).StatusCode);
    }
}
