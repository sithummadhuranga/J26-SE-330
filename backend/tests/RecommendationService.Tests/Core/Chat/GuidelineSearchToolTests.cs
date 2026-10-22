// Follow-up chat: the evidence pack (append-only, stable tags) and the one tool the answering
// model may call. The search itself is a fake here; the gated SQL is tested in Infrastructure.

using WoundRag.Core.Chat.Agent;
using WoundRag.Core.Chat.Evidence;

namespace RecommendationService.Tests.Core.Chat;

public class GuidelineSearchToolTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static EvidencePack Pack() => new([
        new EvidencePassage("S1", "C-1", "h1", "passage one"),
        new EvidencePassage("S2", "C-2", "h2", "passage two"),
    ]);

    private static ToolCall Search(string arguments) => new("call-1", GuidelineSearchTool.Name, arguments);

    [Fact]
    public void The_pack_must_start_with_contiguous_tags()
    {
        Assert.Throws<ArgumentException>(() => new EvidencePack([new EvidencePassage("S2", "C-2", "h2", "x")]));
    }

    [Fact]
    public void A_new_chunk_gets_the_next_tag_and_a_known_chunk_keeps_its_tag()
    {
        var pack = Pack();
        Assert.Equal("S3", pack.AddOrGet("C-9", "h9", "passage nine").Tag);
        Assert.Equal("S1", pack.AddOrGet("C-1", "h1", "passage one").Tag);
        Assert.Equal(["S1", "S2", "S3"], pack.All.Select(p => p.Tag));
        Assert.Equal(["C-9"], pack.AddedSince(2).Select(p => p.ChunkId));
    }

    [Fact]
    public void A_copy_does_not_change_the_original()
    {
        var pack = Pack();
        var copy = pack.Copy();
        copy.AddOrGet("C-9", "h9", "passage nine");
        Assert.Equal(2, pack.All.Count);
        Assert.Equal(3, copy.All.Count);
    }

    [Fact]
    public async Task Found_passages_are_returned_with_their_tags()
    {
        var search = new FakeSearch([new("C-2", "h2", "passage two"), new("C-7", "h7", "passage seven")]);
        var tool = new GuidelineSearchTool(Pack(), search);

        var result = await tool.RunAsync(Search("""{"query": "offloading device"}"""), Ct);

        Assert.Equal("[S2] passage two\n[S3] passage seven", result);
        Assert.Equal([("offloading device", GuidelineSearchTool.MaxResults)], search.Asked);
        Assert.Equal([new SearchRecord("offloading device", 2, 1)], tool.Searches);
    }

    [Fact]
    public async Task No_match_says_so()
    {
        var tool = new GuidelineSearchTool(Pack(), new FakeSearch([]));
        Assert.Equal(GuidelineSearchTool.NoMatch, await tool.RunAsync(Search("""{"query": "x"}"""), Ct));
    }

    [Theory]
    [InlineData("""{"query": "පාදය"}""", GuidelineSearchTool.NotEnglish)]
    [InlineData("""{"query": "  "}""", GuidelineSearchTool.BadArguments)]
    [InlineData("""{"q": "offloading"}""", GuidelineSearchTool.BadArguments)]
    [InlineData("not json", GuidelineSearchTool.BadArguments)]
    public async Task Bad_arguments_are_answered_without_searching(string arguments, string expected)
    {
        var search = new FakeSearch([]);
        var tool = new GuidelineSearchTool(Pack(), search);

        Assert.Equal(expected, await tool.RunAsync(Search(arguments), Ct));
        Assert.Empty(search.Asked);
    }

    [Fact]
    public async Task An_unknown_tool_is_answered_without_searching()
    {
        var search = new FakeSearch([]);
        var tool = new GuidelineSearchTool(Pack(), search);

        Assert.Equal(GuidelineSearchTool.UnknownTool, await tool.RunAsync(new ToolCall("c", "delete_everything", "{}"), Ct));
        Assert.Empty(search.Asked);
    }
}
