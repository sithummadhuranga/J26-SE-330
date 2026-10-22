// The two hashes of decision 72: the whole body, and the body without healingHistory (what Member 4's rebuild never
// changes). Both are taken over a canonical form, so key order and spacing never make a retry look different.

using RecommendationService.Contract;

namespace RecommendationService.Tests.Service;

public class RequestFingerprintTests
{
    private const string Body = """{"caseId":"x","revision":1,"clinicalAssessment":{"pedalPulses":"present","ulcerLocation":"non_plantar"},"healingHistory":[{"areaMm2":480}]}""";

    [Fact]
    public void Key_order_and_spacing_do_not_change_either_hash()
    {
        const string reordered = """
            { "healingHistory": [ { "areaMm2": 480 } ],
              "clinicalAssessment": { "ulcerLocation": "non_plantar", "pedalPulses": "present" },
              "revision": 1, "caseId": "x" }
            """;
        Assert.Equal(RequestFingerprint.Of(Body), RequestFingerprint.Of(reordered));
    }

    [Fact]
    public void A_grown_history_changes_the_body_hash_but_not_the_stable_one()
    {
        var grown = Body.Replace("""[{"areaMm2":480}]""", """[{"areaMm2":500},{"areaMm2":480}]""");
        var (before, after) = (RequestFingerprint.Of(Body), RequestFingerprint.Of(grown));
        Assert.Equal(before.StableSha256, after.StableSha256);
        Assert.NotEqual(before.BodySha256, after.BodySha256);
    }

    [Fact]
    public void Any_other_change_changes_both()
    {
        var other = RequestFingerprint.Of(Body.Replace("\"present\"", "\"absent\""));
        var original = RequestFingerprint.Of(Body);
        Assert.NotEqual(original.StableSha256, other.StableSha256);
        Assert.NotEqual(original.BodySha256, other.BodySha256);
    }

    [Fact]
    public void Array_order_counts_and_hashes_are_lowercase_hex()
    {
        var swapped = RequestFingerprint.Of(Body.Replace("""[{"areaMm2":480}]""", """[{"areaMm2":1},{"areaMm2":2}]"""));
        var back = RequestFingerprint.Of(Body.Replace("""[{"areaMm2":480}]""", """[{"areaMm2":2},{"areaMm2":1}]"""));
        Assert.NotEqual(swapped.BodySha256, back.BodySha256);
        Assert.Matches("^[0-9a-f]{64}$", swapped.StableSha256);
    }
}
