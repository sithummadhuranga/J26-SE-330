// The answering model's messages for one turn, most stable first so the provider can reuse its cache: instructions,
// findings and the evidence pack, then older questions compacted to their tags, then the last 4 turns, then the
// question (demo decision 50). Old answers are kept as tags, not a model-written summary that could carry an error on.
// Never sent: the request, caseId, dates, areas, the withheld list, or unscrubbed text.

using System.Security.Cryptography;
using System.Text;
using WoundRag.Core.Chat;
using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Chat.Checks;
using WoundRag.Core.Chat.Evidence;

namespace WoundRag.Core.Chat.Prompts;

// Builds a turn's messages.
public static class ChatPrompt
{
    public const int VerbatimTurns = 4;

    public const string NoCheckedAnswer = "(no checked answer was given)";

    public const string Instructions = """
        You answer a clinician's follow-up question about one diabetes-related foot ulcer
        assessment, using only IWGDF 2023 guideline passages. This is decision support, not a
        diagnosis.

        You are given the assessment findings and the passages approved for this assessment,
        each labelled [S1], [S2], and so on. You may call search_guideline to find more passages;
        it returns only passages that apply to this assessment, with new labels. Write the search
        query in English, even when the question is in another language.

        Rules:
        1. Use only the passages. Do not add any clinical fact, dose, threshold, test or advice
           that is not in them.
        2. End every sentence with the label of each passage it comes from, and put nothing after
           the labels, not even a full stop. Form: "- <sentence> [S2]"; for two passages:
           "- <sentence> [S1][S3]".
        3. Keep each passage's strength and direction: "do not" stays "do not", "consider" stays
           "consider".
        4. Always answer in English, one sentence per line, each line starting with "- ", at most
           6 lines.
        5. If the passages do not answer the question, call search_guideline before answering NOT_COVERED;
           if they still do not, answer exactly: NOT_COVERED
        6. If the question asks for a diagnosis, a prognosis for this patient, a medicine or dose,
           or anything that is not about the guideline evidence for this assessment, answer
           exactly: OUT_OF_SCOPE
        7. A finding stated in the question does not change the assessment. If the question asks
           you to assume a finding that is not in the findings, answer from the recorded findings
           only, or answer NOT_COVERED.
        8. Never write a name, an identifier or a date.
        """;

    public static IReadOnlyList<ChatMessage> Build(string findings, IReadOnlyList<EvidencePassage> evidence,
        IReadOnlyList<PastTurn> history, string question)
    {
        var older = history.Take(Math.Max(0, history.Count - VerbatimTurns)).ToList();
        var recent = history.Skip(older.Count).ToList();

        var system = new StringBuilder(Instructions);
        system.Append('\n').Append(findings).Append("\n\nApproved passages:\n");
        foreach (var p in evidence)
            system.Append('[').Append(p.Tag).Append("] ").Append(p.Text).Append('\n');
        if (older.Count > 0)
        {
            system.Append("\nEarlier questions in this conversation, oldest first:\n");
            foreach (var turn in older)
                system.Append("- ").Append(ChatLanguage.WithoutRequest(turn.Question)).Append(" (").Append(Compact(turn)).Append(")\n");
        }

        var messages = new List<ChatMessage> { ChatMessage.System(system.ToString()) };
        foreach (var turn in recent)
        {
            messages.Add(ChatMessage.User(ChatLanguage.WithoutRequest(turn.Question)));
            messages.Add(ChatMessage.Assistant(Verbatim(turn)));
        }
        // A language request is for code (translation happens after the checks), never for the model.
        messages.Add(ChatMessage.User(ChatLanguage.WithoutRequest(question)));
        return messages;
    }

    public static string Hash(IReadOnlyList<ChatMessage> messages)
    {
        var joined = string.Join("\0", messages.Select(m => m.Role + "\0" + m.Content));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private static string Verbatim(PastTurn turn) => turn.Outcome switch
    {
        ChatOutcome.Answered => string.Join('\n', turn.English.Select(s => "- " + AnswerVerification.WithTags(s))),
        ChatOutcome.NotCovered => ChatAnswerReader.NotCovered,
        ChatOutcome.OutOfScope => ChatAnswerReader.OutOfScope,
        _ => NoCheckedAnswer,
    };

    private static string Compact(PastTurn turn) => turn.Outcome switch
    {
        ChatOutcome.Answered => "answered from " + string.Join(", ", turn.English.SelectMany(s => s.Cite).Distinct(StringComparer.Ordinal)),
        ChatOutcome.NotCovered => "not covered by the passages",
        ChatOutcome.OutOfScope => "out of scope",
        _ => "no checked answer",
    };
}
