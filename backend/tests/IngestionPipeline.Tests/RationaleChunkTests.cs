// RATIONALE chunks and the decision-13 size rule, on synthetic blocks and on the real PDF.

using IngestionPipeline;
using IngestionPipeline.Corpus;
using WoundRag.Shared.Hashing;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Tests;

public class RationaleChunkTests(ChunkFixture fixture) : IClassFixture<ChunkFixture>
{
    [Theory]
    [InlineData("One. Two? Three! Four", 4)]
    [InlineData("Healing was slower.12,13 The trial ended.", 2)]         // references after the stop
    [InlineData("The mean was 0.05 The next", 1)]                         // a decimal is not a sentence end
    [InlineData("Examples include: • rest • elevation • offloading", 4)]  // bullets
    [InlineData("See e.g. the trial (Smith et al.) for details. Next one.", 2)]
    [InlineData("It worked (see Table 2.) Then it stopped.", 2)]
    public void Sentences_split_where_expected_and_rejoin_exactly(string text, int expected)
    {
        var s = Sentences.Split(text);
        Assert.Equal(expected, s.Count);
        Assert.Equal(text, string.Join(" ", s.Select(x => x.Text)));
        Assert.All(s, x => Assert.Equal(x.Text, text.Substring(x.Offset, x.Text.Length)));
    }

    [Theory]
    [InlineData(new[] { 120, 120, 120, 120 })]
    [InlineData(new[] { 300, 80, 300 })]
    [InlineData(new[] { 390, 20, 20, 390 })]
    [InlineData(new[] { 50, 60, 70, 80, 90, 100, 110, 120, 130, 140 })]
    [InlineData(new[] { 500, 10, 10 })]                                   // one sentence over the cap
    [InlineData(new[] { 200, 200, 200, 30, 30, 30, 200, 90, 90, 90, 90, 90 })]
    public void The_cutting_is_as_good_as_an_exhaustive_search(int[] sentenceWords)
    {
        var counter = new WordPieceCounter(Array.Empty<string>());
        const string header = "H";
        var block = string.Join(" ", sentenceWords.Select(w => "S" + string.Concat(Enumerable.Repeat(" x", w - 1)) + "."));

        var chosen = TextPieces.Cut(block, header, counter);
        Assert.Equal(block, string.Join(" ", chosen.Select(p => p.Text)));

        var s = Sentences.Split(block);
        Assert.Equal(sentenceWords.Length, s.Count);
        int n = s.Count;
        (int, int, int, long) Score(IReadOnlyList<int> tokens)
        {
            var whole = tokens.Count == 1;
            return (tokens.Count(t => t > TextPieces.MaxTokens),
                    tokens.Count(t => t < TextPieces.MinTokens && !whole),
                    tokens.Count,
                    tokens.Sum(t => (long)(t - 325) * (t - 325)));
        }
        (int, int, int, long)? best = null;
        for (int mask = 0; mask < 1 << (n - 1); mask++)
        {
            var tokens = new List<int>();
            int start = 0;
            bool valid = true;
            for (int k = 0; k < n; k++)
            {
                if (k == n - 1 || (mask & (1 << k)) != 0)
                {
                    int t = counter.Count(header + "\n" + string.Join(" ", s.Skip(start).Take(k - start + 1).Select(x => x.Text)));
                    if (t > TextPieces.MaxTokens && k > start) { valid = false; break; }
                    tokens.Add(t);
                    start = k + 1;
                }
            }
            if (!valid) continue;
            var sc = Score(tokens);
            if (best is null || sc.CompareTo(best.Value) < 0) best = sc;
        }
        Assert.Equal(best, Score(chosen.Select(p => p.Tokens).ToList()));
    }

    private static (List<RecommendationChunk> Recs, List<RationaleChunk> Rats) Build(ChunkFixture f)
    {
        var (doc, recs) = f.Get();
        return (recs, RationaleChunks.Build(doc, f.Aliases, f.Sections, WordPieceCounter.Load(RepoPaths.Discover().Vocabulary)));
    }

    [CorpusFact]
    public void There_are_105_rationales_including_the_4_no_recommendation_ones()
    {
        var (_, rats) = Build(fixture);
        Assert.Equal(105, rats.Count(r => r.Piece == 1));
        Assert.Equal(253, rats.Count);   // pieces, measured 4 Oct 2026 on the running text (decision 30)
        var noRec = rats.Where(r => r.OwnerLabel is null && r.Piece == 1).Select(r => r.ChunkId).ToList();
        Assert.Equal(["OFF-RATNR1-1", "OFF-RATNR2-1", "OFF-RATNR3-1", "OFF-RATNR4-1"], noRec);
    }

    [CorpusFact]
    public void Rule2_every_rationale_piece_is_within_the_cap()
    {
        var (_, rats) = Build(fixture);
        Assert.All(rats, r => Assert.True(r.TokenCount <= TextPieces.MaxTokens, $"{r.ChunkId}: {r.TokenCount} tokens"));
        var counter = WordPieceCounter.Load(RepoPaths.Discover().Vocabulary);
        Assert.All(rats, r => Assert.Equal(counter.Count(r.Text), r.TokenCount));
    }

    [CorpusFact]
    public void Rule3_pieces_never_overlap_and_rejoin_to_their_rationale_exactly()
    {
        var (doc, _) = fixture.Get();
        var (_, rats) = Build(fixture);
        foreach (var g in fixture.Aliases.SubGuidelines)
        {
            int noRec = 0;
            foreach (var e in BodyEntries.Read(doc, g.Name, SectionsFile.HeadingsFor(fixture.Sections, g.Name)))
            {
                if (e.Kind == BodyEntryKind.NoRecommendation) noRec++;
                if (e.Rationale is null) continue;
                var prefix = e.Kind == BodyEntryKind.Recommendation
                    ? $"{g.Code}-RAT{RecommendationChunks.PaddedLabel(e.Label!)}-"
                    : $"{g.Code}-RATNR{noRec}-";
                var mine = rats.Where(r => r.ChunkId.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(r => r.Piece).ToList();
                Assert.NotEmpty(mine);
                Assert.Equal(Enumerable.Range(1, mine.Count), mine.Select(r => r.Piece));
                Assert.Equal(e.Rationale.Text, string.Join(" ", mine.Select(r => r.Text[(r.ContextHeader.Length + 1)..])));
                Assert.All(mine, r => Assert.InRange(r.PdfIndex, e.Rationale.FirstPage, e.Rationale.LastPage));
            }
        }
    }

    [CorpusFact]
    public void Rule4_every_recommendation_points_at_the_first_piece_of_its_rationale()
    {
        var (recs, rats) = Build(fixture);
        var byId = rats.ToDictionary(r => r.ChunkId);
        Assert.All(recs, c =>
        {
            var r = byId[c.RationaleChunkId];
            Assert.Equal(1, r.Piece);
            Assert.Equal(c.RationaleOwner, r.OwnerLabel);
            Assert.Equal(c.SubGuideline, r.SubGuideline);
        });
        Assert.DoesNotContain(recs, c => c.RationaleChunkId.Contains("RATNR", StringComparison.Ordinal));
    }

    [CorpusFact]
    public void Rule5_and_6_headers_hashes_and_ids_hold_for_rationale_chunks_too()
    {
        var (recs, rats) = Build(fixture);
        Assert.All(rats, r =>
        {
            Assert.StartsWith(r.ContextHeader + "\n", r.Text);
            Assert.Equal(ChunkHash.Compute(RecommendationChunks.CorpusVersion, r.ChunkId, r.Text), r.ChunkHash);
            Assert.Matches(@"^[A-Z]{3}-RAT(\d{2}[a-h]?|NR\d)-\d+$", r.ChunkId);
        });
        var ids = recs.Select(c => c.ChunkId).Concat(rats.Select(r => r.ChunkId)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var hashes = recs.Select(c => c.ChunkHash).Concat(rats.Select(r => r.ChunkHash)).ToList();
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
        var again = Build(fixture).Rats;
        Assert.Equal(rats.Select(r => r.ChunkHash), again.Select(r => r.ChunkHash));
    }
}
