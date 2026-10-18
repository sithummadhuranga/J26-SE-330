// A stand-in model that answers in the approved format: one placeholder statement per heading, citing that heading's
// first passage. It can forge a tag, and it keeps every prompt it was sent.

using System.Text;
using System.Text.RegularExpressions;
using WoundRag.Core.Generation;

namespace RecommendationService.Tests.Fakes;

public sealed partial class ScriptedWriter(string? forgedTag = null) : ITextGenerator
{
    public string Provider => "test";
    public string Model => "scripted";
    public List<PromptText> Prompts { get; } = [];

    public Task<GeneratorReply> GenerateAsync(PromptText prompt, CancellationToken ct)
    {
        Prompts.Add(prompt);
        var answer = new StringBuilder();
        foreach (var block in prompt.User.Split("\n## ").Skip(1))
        {
            var heading = block[..block.IndexOf('\n')];
            answer.Append("## ").Append(heading).Append('\n');
            if (FirstTag().Match(block) is { Success: true } tag)
                answer.Append("- Placeholder statement [").Append(forgedTag ?? tag.Groups[1].Value).Append("]\n");
        }
        return Task.FromResult(new GeneratorReply(ReplyStatus.Ok, answer.ToString(), "scripted", 10, 5));
    }

    [GeneratedRegex(@"^\[(S[0-9]+)\] ", RegexOptions.Multiline)]
    private static partial Regex FirstTag();
}
