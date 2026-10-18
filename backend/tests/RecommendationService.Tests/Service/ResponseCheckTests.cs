// Every composed body is checked before it is stored (decision 39): a stored body is replayed forever, so a bad one
// must fail now. The check applies Member 4's validator rules and loads the schema beside her validator safely.

using System.Text.Json.Nodes;
using RecommendationService.Contract;

namespace RecommendationService.Tests.Service;

public class ResponseCheckTests
{
    private static readonly Guid CaseId = Guid.Parse("6f1d3c9e-8a2b-4c5d-9e0f-1a2b3c4d5e6f");

    private static JsonObject Valid() => JsonNode.Parse($$"""
        {"contractVersion":"1.0","caseId":"{{CaseId}}","revision":2,"mode":"extractive","retrievalMode":"hybrid",
         "corpusVersion":"iwgdf-2023.r2","sections":[{"heading":"Management","text":"x [S1]","citationTags":["S1"]}],
         "citations":[{"tag":"S1","chunkHash":"abc"}],"withheld":[]}
        """)!.AsObject();

    // Loaded twice and next to her validator (which registers the same $id globally): neither may throw.
    private static readonly ResponseCheck Check = ResponseCheck.FromOutputDirectory();
    private static readonly ResponseCheck SecondCopy = ResponseCheck.FromOutputDirectory();

    [Fact]
    public void A_valid_body_passes_and_agrees_with_member_4s_validator()
    {
        Assert.Null(Check.Problem(Valid(), CaseId, 2));
        Assert.Null(SecondCopy.Problem(Valid(), CaseId, 2));
        Assert.Null(TheirValidator.Check(Valid(), CaseId, 2));
    }

    [Fact]
    public void A_schema_violation_is_a_problem()
    {
        var body = Valid();
        body["mode"] = "creative";
        Assert.NotNull(Check.Problem(body, CaseId, 2));
    }

    [Fact]
    public void An_answer_for_another_case_or_revision_is_a_problem()
    {
        Assert.Contains("revision", Check.Problem(Valid(), CaseId, 3));
        Assert.Contains("case", Check.Problem(Valid(), Guid.NewGuid(), 2));
    }

    [Fact]
    public void A_section_tag_without_its_citation_is_a_problem()
    {
        var body = Valid();
        body["citations"] = new JsonArray();
        Assert.Contains("S1", Check.Problem(body, CaseId, 2));
    }

    [Fact]
    public void The_request_schema_can_be_loaded_more_than_once()
    {
        // RequestReaderTests holds one reader already; a second must still load and still refuse an empty object.
        var second = RequestReader.FromOutputDirectory();
        Assert.NotEmpty(second.Read("{}").Problems);
        Assert.NotEmpty(RequestReaderTests.Reader.Read("{}").Problems);
    }
}
