// List-to-body pairing (decisions 1, 2) on small made-up statements, and the alias file round trip.

using IngestionPipeline.Corpus;

namespace IngestionPipeline.Tests;

public class LabelPairingTests
{
    private static string[] W(string s) => s.Split(' ');

    [Fact]
    public void Similarity_is_2_LCS_over_total_words()
    {
        Assert.Equal(1.0, LabelPairing.Similarity(W("a b c"), W("a b c")));
        Assert.Equal(0.0, LabelPairing.Similarity(W("a b"), W("c d")));
        Assert.Equal(0.8, LabelPairing.Similarity(W("a b c"), W("a c")), 12);   // LCS 2 → 4/5
        Assert.Equal(LabelPairing.Similarity(W("x a y b"), W("a b z")), LabelPairing.Similarity(W("a b z"), W("x a y b")));
    }

    [Fact]
    public void Comparison_ignores_case_punctuation_and_prose_after_the_marker()
    {
        Assert.Equal(LabelPairing.ComparisonWords("Offload the ulcer. (Strong; Low)"),
                     LabelPairing.ComparisonWords("offload THE ulcer (Strong; Low) and then unrelated prose"));
    }

    [Fact]
    public void Pairs_follow_content_even_when_labels_are_shifted()
    {
        var list = new List<(string, string[])> { ("15", W("give drug a")), ("16", W("stop drug b now")), ("17", W("refer to surgeon")) };
        var body = new List<(string, string[])> { ("13", W("give drug a")), ("14", W("stop drug b now")), ("15", W("refer to surgeon")) };

        var p = LabelPairing.Pair("G", list, body);

        Assert.Equal([("15", "13"), ("16", "14"), ("17", "15")], p.Pairs.Select(x => (x.ListLabel, x.BodyLabel)));
        Assert.All(p.Pairs, x => Assert.Equal("identical", x.Basis));
    }

    [Fact]
    public void Near_matches_pair_by_mutual_best_and_are_marked_similar()
    {
        var list = new List<(string, string[])> { ("7", W("use a knee high cast for the foot")) };
        var body = new List<(string, string[])> { ("7a", W("use a knee high cast for a foot")), ("8", W("educate the person")) };

        var p = LabelPairing.Pair("G", list, body);

        var pair = Assert.Single(p.Pairs);
        Assert.Equal(("7", "7a", "similar"), (pair.ListLabel, pair.BodyLabel, pair.Basis));
        Assert.Equal("8", Assert.Single(p.BodyOnly).Label);
    }

    [Fact]
    public void Nothing_below_the_floor_is_proposed_the_best_candidate_is_only_a_hint()
    {
        var list = new List<(string, string[])> { ("8b", W("check shoes daily")) };
        var body = new List<(string, string[])> { ("8", W("screen annually for neuropathy")) };

        var p = LabelPairing.Pair("G", list, body);

        Assert.Empty(p.Pairs);
        var only = Assert.Single(p.ListOnly);
        Assert.Equal(("8b", "8"), (only.Label, only.BestOther));
        Assert.True(only.BestSimilarity < LabelPairing.ProposalFloor);
    }

    [Fact]
    public void Two_list_items_with_the_same_text_are_not_both_paired()
    {
        var list = new List<(string, string[])> { ("13", W("select an antibiotic")), ("15", W("select an antibiotic")) };
        var body = new List<(string, string[])> { ("13", W("select an antibiotic")) };

        var p = LabelPairing.Pair("G", list, body);

        Assert.Equal(("13", "13"), (Assert.Single(p.Pairs).ListLabel, p.Pairs[0].BodyLabel));
        Assert.Equal("15", Assert.Single(p.ListOnly).Label);   // surfaced for review, never silently merged
    }

    [Fact]
    public void The_proposal_does_not_depend_on_input_order()
    {
        var list = new List<(string, string[])> { ("1", W("a b c d")), ("2", W("e f g h")), ("3", W("i j k l")) };
        var body = new List<(string, string[])> { ("1", W("a b c x")), ("2", W("e f g y")), ("3", W("i j k z")) };

        var forward = LabelPairing.Pair("G", list, body).Pairs.Select(x => (x.ListLabel, x.BodyLabel)).Order().ToList();
        list.Reverse();
        body.Reverse();
        var reversed = LabelPairing.Pair("G", list, body).Pairs.Select(x => (x.ListLabel, x.BodyLabel)).Order().ToList();

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void Alias_file_round_trips_and_codes_and_labels_stay_strings()
    {
        var pairing = new List<GuidelinePairing>
        {
            new("IWGDF Offloading Guideline", [new ProposedPair("1", "1", 1.0, "identical")], [], []),
        };
        var path = Path.Combine(Path.GetTempPath(), $"aliases-{Guid.NewGuid():N}.yaml");
        try
        {
            AliasFile.Write(path, AliasFile.ToProposal(pairing, new string('a', 64), new DateOnly(2026, 9, 28)));
            var text = File.ReadAllText(path);
            Assert.Contains("\"OFF\"", text);
            Assert.Contains("\"list\": \"1\"", text);

            var back = AliasFile.Read(path)!;
            Assert.Equal("proposed", back.Status);
            Assert.Equal("OFF", back.SubGuidelines[0].Code);
            Assert.Equal(("1", "1"), (back.SubGuidelines[0].Pairs[0].List, back.SubGuidelines[0].Pairs[0].Body));

            File.WriteAllText(path, text.Replace("\"status\": \"proposed\"", "status: confirmed"));
            Assert.Equal("confirmed", AliasFile.Read(path)!.Status);

            var edited = File.ReadAllText(path);
            Assert.Contains("\"excluded\": []", edited);
            File.WriteAllText(path, edited.Replace("\"excluded\": []", "excluded:\n  - label: \"15\"\n    reason: \"misprint\""));
            Assert.Equal("15", Assert.Single(AliasFile.Read(path)!.SubGuidelines[0].Excluded).Label);

            File.AppendAllText(path, "  \"excluded\": []\n");
            Assert.Throws<YamlDotNet.Core.YamlException>(() => AliasFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_confirmed_alias_file_resolves_every_label_once()
    {
        var file = AliasFile.Read(Path.Combine(IngestionPipeline.RepoPaths.Discover().Patterns, "label_aliases.yaml"))!;

        Assert.Equal("confirmed", file.Status);
        Assert.False(string.IsNullOrWhiteSpace(file.ConfirmedBy));
        Assert.Equal(7, file.SubGuidelines.Select(g => g.Code).Distinct().Count());
        foreach (var g in file.SubGuidelines)
        {
            var listLabels = g.Pairs.Select(p => p.List).Concat(g.ListOnly.Select(u => u.Label)).Concat(g.Excluded.Select(e => e.Label)).ToList();
            Assert.Equal(listLabels.Count, listLabels.Distinct().Count());   // no list label in two places
            Assert.Empty(g.BodyOnly);
            Assert.All(g.Excluded, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));
        }
        Assert.Equal(151, file.SubGuidelines.Sum(g => g.Pairs.Count + g.ListOnly.Count));
        Assert.Equal(("INF", "15"), file.SubGuidelines.SelectMany(g => g.Excluded.Select(e => (g.Code, e.Label))).Single());
    }
}
