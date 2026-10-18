// Builds the prompt (decision 38). Only three things reach the model: these instructions, the stored guideline
// passages and the findings line. Never the request, caseId, dates, areas, PAR, images or free text.

using System.Text;

namespace WoundRag.Core.Generation;

public static class PromptBuilder
{
    // Exactly the approved text: PromptTests pins its SHA-256, so a change here must be re-approved.
    public const string Instructions = """
        You write the protocol part of a clinical decision-support answer for a diabetes-related
        foot ulcer, for a clinician to read. You are given guideline passages, each labelled [S1],
        [S2], and so on, grouped under four headings, and a summary of the assessment findings.

        Rules:
        1. Use only the passages. Do not add any clinical fact, dose, threshold, test or advice that
           is not in them.
        2. Write each sentence under the heading its passage is listed under. Never move a passage
           to another heading.
        3. End every sentence with the tag of each passage it comes from, and put nothing after the
           tags, not even a full stop. Form: "<sentence based on passage S2> [S2]"; for two
           passages: "<sentence> [S1][S3]".
        4. Keep each passage's strength and direction: "do not" stays "do not", "consider" stays
           "consider".
        5. Use the findings only to choose which passages to put first. Do not repeat the findings.
        6. If a heading has no passages, leave it empty.
        7. Keep the protocol short: at most 6 sentences under management, 4 under
           contraindications, 3 under trajectory and 4 under assessmentInstructions. Choose the
           passages that matter most for the findings, and combine passages that say the same
           thing into one sentence with all their tags.

        Answer with the four headings in this order, each followed by its sentences, one per line,
        each line starting with "- ":
        ## management
        ## contraindications
        ## trajectory
        ## assessmentInstructions
        """;

    public static PromptText Build(string findings, IReadOnlyList<PromptSection> sections)
    {
        var user = new StringBuilder().Append(findings).Append('\n');
        foreach (var section in sections)
        {
            user.Append('\n').Append("## ").Append(section.Heading).Append('\n');
            foreach (var passage in section.Passages)
                user.Append('[').Append(passage.Tag).Append("] ").Append(passage.Text).Append('\n');
        }
        return new PromptText(Instructions, user.ToString());
    }
}
