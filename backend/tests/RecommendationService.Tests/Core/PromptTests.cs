// The prompt (decision 38): the approved instructions are pinned by hash, the user message holds only the findings
// line and the tagged passages, and the findings line is fixed phrases only, never a measured number.

using System.Security.Cryptography;
using System.Text;
using WoundRag.Core.Gating;
using WoundRag.Core.Generation;
using WoundRag.Core.Healing;
using WoundRag.Core.Retrieval;

namespace RecommendationService.Tests.Core;

public class PromptTests
{
    // SHA-256 of the instruction text approved in the demo's decision 38 with its second amendment (7 Oct 2026).
    private const string ApprovedInstructionsSha256 = "a3152e7f3f444cd806670ef69ad658c0dddce94b3cae8ffef003a90c2f0b58d3";

    private static readonly StalledHealingRule R2Rule = new(StalledHealingRule.PadBps15, 50, 4);

    [Fact]
    public void The_instructions_are_the_approved_text()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PromptBuilder.Instructions)));
        Assert.Equal(ApprovedInstructionsSha256, hash);
        Assert.DoesNotContain("\r", PromptBuilder.Instructions);
    }

    [Fact]
    public void The_instructions_name_the_response_sections_in_order()
    {
        var listed = PromptBuilder.Instructions.Split('\n').Where(l => l.StartsWith("## ", StringComparison.Ordinal)).Select(l => l[3..]);
        Assert.Equal(ResponseSections.All, listed);
    }

    [Fact]
    public void The_user_message_is_the_findings_then_each_heading_with_its_tagged_passages()
    {
        var prompt = PromptBuilder.Build("Findings: x. Healing: not stalled.",
        [
            new("management", [new("S1", "[H1]\nbody one"), new("S2", "[H2]\nbody two")]),
            new("contraindications", []),
        ]);

        Assert.Equal(PromptBuilder.Instructions, prompt.System);
        Assert.Equal("Findings: x. Healing: not stalled.\n\n## management\n[S1] [H1]\nbody one\n[S2] [H2]\nbody two\n\n## contraindications\n",
            prompt.User);
        Assert.Matches("^sha256:[0-9a-f]{64}$", prompt.Hash);
        Assert.NotEqual(prompt.Hash, new PromptText(prompt.System + prompt.User, "").Hash);   // the two parts stay apart
    }

    private static Dictionary<string, string> Slots(string healingState) =>
        QuerySlots.Names.ToDictionary(n => n, n => n == QuerySlots.HealingState ? healingState : GateFields.NotRecorded);

    [Fact]
    public void Every_word_the_corpus_holds_has_a_phrase_and_no_measured_number_appears()
    {
        foreach (var (slot, words) in SlotVocabularyTests.R2Words)
            foreach (var word in words)
            {
                var slots = Slots(QuerySlots.NotStalled);
                slots[slot] = word;
                var line = FindingsSummary.Build(slots, R2Rule);
                Assert.StartsWith("Findings: ", line);
                // The one number allowed is the rule's own threshold in the stalled phrase.
                Assert.DoesNotMatch("[0-9]", line.Replace("less than 50% after 4 weeks", ""));
            }
    }

    [Fact]
    public void The_findings_line_reads_as_expected()
    {
        var slots = new Dictionary<string, string>
        {
            [GateFields.UlcerLocation] = "plantar_forefoot",
            [GateFields.ProtectiveSensation] = "absent",
            [GateFields.PedalPulses] = GateFields.NotRecorded,
            [GateFields.ProbeToBone] = "positive",
            [GateFields.InfectionGrade] = "moderate",
            [QuerySlots.HealingState] = QuerySlots.Stalled,
        };
        Assert.Equal("Findings: ulcer on the plantar forefoot; protective sensation absent; pedal pulses not recorded; " +
                     "probe-to-bone test positive; infection moderate. Healing: stalled (area reduced by less than 50% after 4 weeks or more).",
            FindingsSummary.Build(slots, R2Rule));
    }

    [Fact]
    public void The_stalled_phrase_quotes_the_corpus_rule_not_a_number_in_code()
    {
        var line = FindingsSummary.Build(Slots(QuerySlots.Stalled), new StalledHealingRule(StalledHealingRule.PadBps15, 40, 6));
        Assert.EndsWith("Healing: stalled (area reduced by less than 40% after 6 weeks or more).", line);
    }

    [Fact]
    public void An_unknown_value_is_refused_not_passed_through()
    {
        var slots = Slots(QuerySlots.NotStalled);
        slots[GateFields.InfectionGrade] = "patient says it hurts";
        Assert.Throws<ArgumentException>(() => FindingsSummary.Build(slots, R2Rule));
        Assert.Throws<ArgumentException>(() => FindingsSummary.Build(Slots("unknown"), R2Rule));
        slots = Slots(QuerySlots.NotStalled);
        slots.Remove(GateFields.PedalPulses);
        Assert.Throws<ArgumentException>(() => FindingsSummary.Build(slots, R2Rule));
    }
}
