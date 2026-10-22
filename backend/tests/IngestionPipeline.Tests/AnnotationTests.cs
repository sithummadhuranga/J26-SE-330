// Plan step A4 with the confirmed annotation.yaml: clause, Req(d), mentioned fields and polarity on made-up
// sentences, then the measured numbers of decision 21's addendum on the real PDF (skipped without it).

using IngestionPipeline;
using IngestionPipeline.Corpus;

namespace IngestionPipeline.Tests;

public class AnnotationTests(ChunkFixture fixture) : IClassFixture<ChunkFixture>
{
    private static readonly AnnotationFileDto File_ =
        AnnotationFile.Read(Path.Combine(RepoPaths.Discover().Patterns, "annotation.yaml"))!;

    private static readonly Annotator A = new(File_);

    private static string ClauseA(string s) => A.ClauseA(s) is { } c ? s[c.Start..c.End] : "";

    [Fact]
    public void Every_field_in_the_file_is_in_the_decision_7_vocabulary()
    {
        Assert.All(File_.FieldTerms.Keys, f => Assert.Contains(f, Annotator.Vocabulary));
    }

    [Fact]
    public void A_field_outside_the_vocabulary_is_refused()
    {
        var bad = AnnotationFile.Read(Path.Combine(RepoPaths.Discover().Patterns, "annotation.yaml"))!;
        bad.FieldTerms["woundBedLabels"] = ["slough"];
        Assert.Throws<InvalidDataException>(() => new Annotator(bad));
    }

    [Fact]
    public void An_unknown_primary_variant_is_refused()
    {
        var bad = AnnotationFile.Read(Path.Combine(RepoPaths.Discover().Patterns, "annotation.yaml"))!;
        bad.PrimaryVariant = "B";
        Assert.Throws<InvalidDataException>(() => new Annotator(bad));
    }

    [Fact]
    public void The_file_is_confirmed_with_the_union()
    {
        Assert.Equal(("confirmed", "union", "2026-10-04"), (File_.Status, File_.PrimaryVariant, File_.ConfirmedOn));
    }

    [Fact]
    public void A_list_comma_does_not_end_the_clause_but_a_main_clause_verb_does()
    {
        Assert.Equal("In a person with diabetes, peripheral artery disease and a foot ulcer",
            ClauseA("In a person with diabetes, peripheral artery disease and a foot ulcer, consider a test"));
    }

    [Fact]
    public void A_subject_and_modal_end_the_clause()
    {
        Assert.Equal("In a person with diabetes and gangrene",
            ClauseA("In a person with diabetes and gangrene, a procedure should be considered"));
    }

    [Fact]
    public void An_infinitive_ends_the_clause_but_to_a_does_not()
    {
        var s = "Instruct a person with diabetes to inspect the plantar surface";
        var c = A.ClausesC(s).Single();
        Assert.Equal("a person with diabetes", s[c.Start..c.End]);
        Assert.Empty(A.FieldsIn(s, [(c.Start, c.End)]));

        var t = "Refer a person with a neuropathic ulcer to a specialist";
        var d = A.ClausesC(t).Single();
        Assert.Equal("a person with a neuropathic ulcer to a specialist", t[d.Start..d.End]);
    }

    [Fact]
    public void A_frequency_adverb_ends_the_clause()
    {
        var s = "Screen a person with diabetes annually for signs of neuropathy";
        var c = A.ClausesC(s).Single();
        Assert.Equal("a person with diabetes", s[c.Start..c.End]);
    }

    [Fact]
    public void With_is_not_a_variant_A_opener()
    {
        Assert.Null(A.ClauseA("With the exception of one dressing, we suggest not using others"));
    }

    [Fact]
    public void A_field_inside_the_clause_gates_and_outside_it_is_only_mentioned()
    {
        var s = "In a person with diabetes and an infected ulcer, consider checking for ischaemia";
        var a = A.ClauseA(s)!;
        Assert.Equal(["infectionGrade"], A.FieldsIn(s, [(a.Start, a.End)]));
        Assert.Equal(["pedalPulses"], A.FieldsIn(s, [(a.End, s.Length)]));
    }

    [Fact]
    public void Charcot_neuro_osteoarthropathy_is_not_neuropathy()
    {
        var s = "In a person with active Charcot neuro-osteoarthropathy, consider a cast";
        var a = A.ClauseA(s)!;
        Assert.DoesNotContain("protectiveSensation", A.FieldsIn(s, [(a.Start, a.End)]));
    }

    [Fact]
    public void Toe_pressure_is_not_a_location()
    {
        var s = "In a person with diabetes and a toe pressure below a value, consider imaging";
        var a = A.ClauseA(s)!;
        Assert.DoesNotContain("ulcerLocation", A.FieldsIn(s, [(a.Start, a.End)]));
    }

    [Fact]
    public void A_field_named_only_after_a_qualifier_does_not_gate()
    {
        var n = A.Annotate(Synthetic("For diagnosing a possible infected ulcer in a person with diabetes, use a test (Strong; Low)"));
        Assert.Empty(n.ReqA);
        Assert.Contains("infectionGrade", n.MentionedA);
        Assert.Contains("field after a qualifier does not gate (suspected/possible/diagnosing)", n.Flags);
    }

    [Fact]
    public void A_field_named_with_and_without_a_qualifier_still_gates()
    {
        var n = A.Annotate(Synthetic("In a person with an infected bone or a suspected infected ulcer, use a test (Strong; Low)"));
        Assert.Equal(["infectionGrade"], n.ReqA);
        Assert.DoesNotContain("infectionGrade", n.MentionedA);
    }

    [Fact]
    public void The_union_holds_every_field_either_variant_gates_on()
    {
        var n = new ChunkAnnotation("TST-R01", null, [], ["pedalPulses"], ["infectionGrade", "pedalPulses"], [], [], [], "RECOMMEND", []);
        Assert.Equal(["pedalPulses"], Annotator.Required(n, "A"));
        Assert.Equal(["infectionGrade", "pedalPulses"], Annotator.Required(n, "C"));
        Assert.Equal(["infectionGrade", "pedalPulses"], Annotator.Required(n, "union"));
    }

    [Fact]
    public void The_authors_prerequisites_are_added_under_every_variant()
    {
        var n = new ChunkAnnotation("TST-R01", null, [], [], [], ["healingTrajectory"], [], [], "RECOMMEND", []);
        Assert.All(Annotator.Variants, v => Assert.Equal(["healingTrajectory"], Annotator.Required(n, v)));
    }

    [Fact]
    public void An_author_prerequisite_outside_the_vocabulary_is_refused()
    {
        var bad = AnnotationFile.Read(Path.Combine(RepoPaths.Discover().Patterns, "annotation.yaml"))!;
        bad.AuthorPrerequisites["TST-R01"] = ["woundBedLabels"];
        Assert.Throws<InvalidDataException>(() => new Annotator(bad));
    }

    [Theory]
    [InlineData("Do not use a dressing for this purpose", "PROHIBIT")]
    [InlineData("We suggest not using a dressing for this purpose", "PROHIBIT")]
    [InlineData("We do not suggest using a dressing for this purpose", "PROHIBIT")]
    [InlineData("In a person with diabetes and a foot ulcer, do not use a dressing", "PROHIBIT")]
    [InlineData("In a person with diabetes and a foot ulcer, avoid a dressing", "PROHIBIT")]
    [InlineData("With the exception of one dressing, we suggest not using others", "PROHIBIT")]
    [InlineData("In a person whose ulcer has not healed, consider a dressing", "RECOMMEND")]
    [InlineData("Consider a dressing when others are not available", "RECOMMEND")]
    public void Polarity_is_read_from_the_main_clause(string statement, string expected)
    {
        var chunk = Synthetic(statement + " (Strong; Low)");
        Assert.Equal(expected, A.Annotate(chunk).Polarity);
    }

    [Fact]
    public void The_grade_marker_is_cut_with_or_without_its_parenthesis()
    {
        Assert.Equal("Use a dressing.", Annotator.BareStatement("[h]\nUse a dressing. (Strong; Low)"));
        Assert.Equal("Use a dressing.", Annotator.BareStatement("[h]\nUse a dressing. Best Practice Statement"));
        Assert.Equal("Use a (wide) dressing.", Annotator.BareStatement("[h]\nUse a (wide) dressing. Best Practice Statement"));
    }

    private static RecommendationChunk Synthetic(string statement) => new(
        "TST-R01", "1", "Test", "S", "[h]", "[h]\n" + statement, "", 1, null, new string('0', 64), 1,
        "Strong", "Low", false, "1", "1", [], false, null, "1", "TST-RAT01-1");

    [CorpusFact]
    public void Corpus_distribution_matches_the_measured_proposal()
    {
        var (_, chunks) = fixture.Get();
        var notes = chunks.Select(A.Annotate).ToList();
        Assert.Equal(151, notes.Count);
        Assert.Equal(64, notes.Count(n => n.ClauseA is not null));
        Assert.Equal(108, notes.Count(n => n.ClausesC.Count > 0));
        Assert.Equal(39, notes.Count(n => n.ReqA.Count > 0));
        Assert.Equal(49, notes.Count(n => n.ReqC.Count > 0));
        Assert.Equal(55, notes.Count(n => Annotator.Required(n, "union").Count > 0));
        Assert.Equal(38, notes.Count(n => n.Polarity == "PROHIBIT"));
        Assert.All(notes, n => Assert.All(n.ReqA.Concat(n.ReqC), f => Assert.Contains(f, Annotator.Vocabulary)));
    }

    [CorpusFact]
    public void Screening_and_self_monitoring_actions_do_not_become_prerequisites()
    {
        var (_, chunks) = fixture.Get();
        foreach (var id in new[] { "PRE-R01", "PRE-R07" })
        {
            var n = A.Annotate(chunks.Single(c => c.ChunkId == id));
            Assert.Empty(n.ReqA);
            Assert.Empty(n.ReqC);
        }
    }

    [CorpusFact]
    public void The_authors_decisions_of_4_October_hold_on_the_corpus()
    {
        var (_, chunks) = fixture.Get();
        List<string> Union(string id) => Annotator.Required(A.Annotate(chunks.Single(c => c.ChunkId == id)), "union");
        foreach (var id in new[] { "INF-R03", "INF-R04", "INF-R05", "INF-R10", "INF-R21" })
            Assert.Empty(Union(id));
        Assert.Equal(["infectionGrade"], Union("INF-R12d"));
    }

    [CorpusFact]
    public void The_author_list_names_only_statements_with_no_clause()
    {
        var (_, chunks) = fixture.Get();
        Assert.Equal(5, File_.AuthorPrerequisites.Count);
        foreach (var (id, fields) in File_.AuthorPrerequisites)
        {
            var n = A.Annotate(chunks.Single(c => c.ChunkId == id));
            Assert.Null(n.ClauseA);
            Assert.Empty(n.ClausesC);
            Assert.Equal(fields.Order(StringComparer.Ordinal), Annotator.Required(n, "union"));
        }
    }

    [CorpusFact]
    public void The_stalled_healing_thresholds_are_located_in_PAD_best_practice_statement_15()
    {
        var (_, chunks) = fixture.Get();
        var t = Annotator.LocateThresholds(File_, chunks);
        Assert.Equal(["pad-bps15.par_percent", "pad-bps15.weeks"], t.Select(x => x.ThresholdId));
        Assert.All(t, x => Assert.Equal(("PAD-R15", 176), (x.ChunkId, x.PdfIndex)));
        Assert.All(t, x => Assert.Contains(x.Value == 50 ? "50%" : "4 weeks", x.QuotedSource));
        Assert.True(chunks.Single(c => c.ChunkId == "PAD-R15").IsBestPractice);
    }
}
