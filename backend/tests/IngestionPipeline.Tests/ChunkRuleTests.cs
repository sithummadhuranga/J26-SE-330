// The chunk rules on the real PDF: counts, IDs, labels, grades, splits, rationale links and hashes.

using System.Text.RegularExpressions;
using IngestionPipeline;
using IngestionPipeline.Corpus;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;
using WoundRag.Shared.Hashing;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Tests;

// Builds the recommendation chunks once, from the running text the chunk command reads (decision 30).
public sealed class ChunkFixture
{
    public GuidelineDocument? Doc { get; }
    public AliasFileDto Aliases { get; }
    public SectionsFileDto Sections { get; }
    public List<RecommendationChunk>? Chunks { get; }

    public ChunkFixture()
    {
        var paths = RepoPaths.Discover();
        Aliases = AliasFile.Read(Path.Combine(paths.Patterns, "label_aliases.yaml"))!;
        Sections = SectionsFile.Read(Path.Combine(paths.Patterns, "sections.yaml"))!;
        if (CorpusFiles.MissingReason() is not null) return;
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        Doc = TableChunks.RunningText(pdf, GuidelineDocument.Load(pdf), paths.Corpus);
        Chunks = RecommendationChunks.Build(Doc, Aliases, Sections, WordPieceCounter.Load(paths.Vocabulary));
    }

    public (GuidelineDocument Doc, List<RecommendationChunk> Chunks) Get() =>
        (Doc ?? throw new InvalidOperationException("corpus files are missing"), Chunks!);
}

public class ChunkRuleTests(ChunkFixture fixture) : IClassFixture<ChunkFixture>
{
    private static string[] Words(string s) => Regex.Matches(s.ToLowerInvariant(), @"\w+").Select(m => m.Value).ToArray();

    private static string Statement(RecommendationChunk c) => c.Text[(c.ContextHeader.Length + 1)..];

    [CorpusFact]
    public void There_are_151_recommendation_chunks_125_graded_and_26_best_practice()
    {
        var (_, chunks) = fixture.Get();
        Assert.Equal(151, chunks.Count);
        Assert.Equal(125, chunks.Count(c => !c.IsBestPractice));
        Assert.Equal(26, chunks.Count(c => c.IsBestPractice));
        Assert.All(chunks, c => Assert.True(c.IsBestPractice == (c.GradeStrength is null && c.GradeCertainty is null)));
    }

    [CorpusFact]
    public void Rule1_every_body_statement_is_whole_in_its_chunk_or_exactly_covered_by_its_sub_items()
    {
        var (doc, chunks) = fixture.Get();
        foreach (var g in fixture.Aliases.SubGuidelines)
        {
            foreach (var e in BodyEntries.Read(doc, g.Name, SectionsFile.HeadingsFor(fixture.Sections, g.Name)).Where(e => e.Kind == BodyEntryKind.Recommendation))
            {
                var mine = chunks.Where(c => c.SubGuideline == g.Name && c.BodyLabel == e.Label).ToList();
                if (mine.Count == 1)
                {
                    Assert.Equal(Words(e.Statement), Words(Statement(mine[0])));
                }
                else
                {
                    var block = BlockSplitter.Split(e.Statement, mine.Select(c => c.RecommendationId[^1..]).ToList());
                    var rebuilt = Words(block.Stem).Concat(block.Items.SelectMany(i => Words(i.Connector).Append(i.Letter).Concat(Words(i.Text))));
                    Assert.Equal(Words(e.Statement), rebuilt);
                    Assert.All(mine, c => Assert.StartsWith(block.Stem, Statement(c)));
                }
            }
        }
    }

    [CorpusFact]
    public void The_chunker_reads_the_same_statement_words_as_the_structure_parser()
    {
        var (doc, _) = fixture.Get();
        foreach (var g in fixture.Aliases.SubGuidelines)
        {
            var idxs = doc.Positions(g.Name);
            var lst = doc.FindSection(idxs, "LIST OF RECOMMENDATIONS", Patterns.ListTerminators)!.Value;
            var bod = doc.FindSection(idxs, "RECOMMENDATIONS", Patterns.BodyTerminators, after: lst.Hi)!.Value;
            var parsed = doc.ParseBodyRecommendations(idxs, bod.Lo, bod.Hi);
            var mine = BodyEntries.Read(doc, g.Name, SectionsFile.HeadingsFor(fixture.Sections, g.Name)).Where(e => e.Kind == BodyEntryKind.Recommendation).ToList();
            Assert.Equal(parsed.Select(r => r.Label), mine.Select(e => e.Label));
            Assert.All(parsed.Zip(mine), p => Assert.Equal(Words(p.First.Statement), Words(p.Second.Statement)));
        }
    }

    [CorpusFact]
    public void Rule4_rationale_links_are_many_to_one_and_always_resolve()
    {
        var (doc, chunks) = fixture.Get();
        Assert.Equal(46, chunks.Select(c => (c.SubGuideline, c.BodyLabel, c.RationaleOwner)).Distinct().Count(x => x.BodyLabel != x.RationaleOwner));
        foreach (var c in chunks)
        {
            var owner = BodyEntries.Read(doc, c.SubGuideline, SectionsFile.HeadingsFor(fixture.Sections, c.SubGuideline)).Single(e => e.Kind == BodyEntryKind.Recommendation && e.Label == c.RationaleOwner);
            Assert.NotNull(owner.Rationale);
        }
        Assert.Contains(chunks.GroupBy(c => (c.SubGuideline, c.RationaleOwner)), grp => grp.Count() > 1);
    }

    [CorpusFact]
    public void Rule5_every_chunk_opens_with_its_context_header_and_the_hash_covers_it()
    {
        var (_, chunks) = fixture.Get();
        Assert.All(chunks, c =>
        {
            Assert.Equal($"[IWGDF 2023 > {c.SubGuideline} > {c.Section}]", c.ContextHeader);
            Assert.StartsWith(c.ContextHeader + "\n", c.Text);
            Assert.Equal(ChunkHash.Compute(RecommendationChunks.CorpusVersion, c.ChunkId, c.Text), c.ChunkHash);
            Assert.NotEqual(ChunkHash.Compute(RecommendationChunks.CorpusVersion, c.ChunkId, Statement(c)), c.ChunkHash);
        });
    }

    [CorpusFact]
    public void Rule6_chunk_ids_follow_the_scheme_are_unique_and_every_label_resolves_once()
    {
        var (_, chunks) = fixture.Get();
        Assert.All(chunks, c => Assert.Matches(@"^[A-Z]{3}-R\d{2}[a-h]?$", c.ChunkId));
        Assert.Equal(chunks.Count, chunks.Select(c => c.ChunkId).Distinct().Count());
        Assert.Equal(chunks.Count, chunks.Select(c => c.ChunkHash).Distinct().Count());
        foreach (var g in fixture.Aliases.SubGuidelines)
            foreach (var p in g.Pairs)
                Assert.Single(chunks, c => c.SubGuideline == g.Name && c.ListLabel == p.List);
        Assert.Equal("OFF-R07a", RecommendationChunks.ChunkIdFor("OFF", "7a"));
        Assert.Equal("PRE-R08b", RecommendationChunks.ChunkIdFor("PRE", "8b"));
    }

    [CorpusFact]
    public void Rule6_building_twice_gives_identical_chunks()
    {
        var (doc, chunks) = fixture.Get();
        var again = RecommendationChunks.Build(doc, fixture.Aliases, fixture.Sections, WordPieceCounter.Load(RepoPaths.Discover().Vocabulary));
        Assert.Equal(chunks.Select(c => (c.ChunkId, c.ChunkHash)), again.Select(c => (c.ChunkId, c.ChunkHash)));
    }

    [CorpusFact]
    public void Rule7_body_wording_is_canonical_and_the_list_wording_is_kept_but_not_hashed()
    {
        var (_, chunks) = fixture.Get();
        Assert.All(chunks, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.VariantText));
            Assert.Equal(!Words(c.VariantText).SequenceEqual(Words(Statement(c))), c.TextDivergence);
        });
        Assert.Contains(chunks, c => c.TextDivergence);
        Assert.Contains(chunks, c => !c.TextDivergence);
    }

    [CorpusFact]
    public void The_four_no_recommendation_entries_are_found_and_never_become_recommendations()
    {
        var (doc, chunks) = fixture.Get();
        var noRec = fixture.Aliases.SubGuidelines.SelectMany(g => BodyEntries.Read(doc, g.Name, SectionsFile.HeadingsFor(fixture.Sections, g.Name)))
            .Where(e => e.Kind == BodyEntryKind.NoRecommendation).ToList();
        Assert.Equal(4, noRec.Count);
        Assert.All(noRec, e => Assert.NotNull(e.Rationale));
        Assert.Equal([214, 224, 226, 226], noRec.Select(e => e.FirstPage));
        Assert.All(noRec, e => Assert.Null(e.Label));
        Assert.Equal(151, chunks.Count);   // none of the four was counted

        const string off = "IWGDF Offloading Guideline";
        var idxs = doc.Positions(off);
        var lst = doc.FindSection(idxs, "LIST OF RECOMMENDATIONS", Patterns.ListTerminators)!.Value;
        var bod = doc.FindSection(idxs, "RECOMMENDATIONS", Patterns.BodyTerminators, after: lst.Hi)!.Value;
        var parsed = doc.ParseBodyRecommendations(idxs, bod.Lo, bod.Hi);
        var mine = BodyEntries.Read(doc, off, SectionsFile.HeadingsFor(fixture.Sections, off));
        foreach (var label in new[] { "2", "7c" })
        {
            var parsedWords = Words(parsed.Single(r => r.Label == label).Rationale);
            var myWords = Words(mine.Single(e => e.Label == label).Rationale!.Text);
            Assert.True(myWords.Length < parsedWords.Length);
            Assert.Equal(myWords, parsedWords.Take(myWords.Length));
        }
    }

    [Theory]
    [InlineData("a non-", "removable device", "a non-removable device")]
    [InlineData("a knee-high", "walker", "a knee-high walker")]
    [InlineData("see Table", "1", "see Table 1")]
    [InlineData("pre-", "Operative", "pre- Operative")]    // capitalised continuation: kept as two words
    [InlineData("", "first", "first")]
    public void Lines_join_with_one_space_except_a_hyphen_before_a_lowercase_continuation(string text, string next, string expected) =>
        Assert.Equal(expected, LineJoin.Join(text, next));
}
