// PROSE chunks: only sections sections.yaml marks prose, every heading listed in order (decision 14).

using IngestionPipeline;
using IngestionPipeline.Corpus;
using WoundRag.Shared.Hashing;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Tests;

public class ProseChunkTests(ChunkFixture fixture) : IClassFixture<ChunkFixture>
{
    private static WordPieceCounter Counter() => WordPieceCounter.Load(RepoPaths.Discover().Vocabulary);

    private ProseResult Build(SectionsFileDto? sections = null)
    {
        var (doc, _) = fixture.Get();
        return ProseChunks.Build(doc, fixture.Aliases, sections ?? fixture.Sections, Counter());
    }

    [Fact]
    public void The_sections_file_is_confirmed_and_lists_the_seven_sub_guidelines()
    {
        Assert.Equal("confirmed", fixture.Sections.Status);
        Assert.Equal(fixture.Aliases.SubGuidelines.Select(g => g.Name), fixture.Sections.SubGuidelines.Select(g => g.Name));
        Assert.All(fixture.Sections.SubGuidelines.SelectMany(g => g.Headings), h =>
        {
            Assert.Contains(h.Kind, new[] { "prose", "exclude", "sub" });
            Assert.Equal(h.Kind == "exclude", !string.IsNullOrWhiteSpace(h.Reason));
        });
    }

    [CorpusFact]
    public void The_note_after_the_infection_list_is_the_only_prose_under_the_list_heading()
    {
        var r = Build();
        var c = Assert.Single(r.Chunks, c => c.Section == "LIST OF RECOMMENDATIONS");
        Assert.Equal(("INF-P001", 108), (c.ChunkId, c.PdfIndex));
        Assert.StartsWith("Note:", c.Text[(c.ContextHeader.Length + 1)..]);
    }

    [CorpusFact]
    public void Rule2_every_prose_piece_is_within_the_cap()
    {
        var r = Build();
        Assert.Equal(109, r.Chunks.Count);   // measured 4 Oct 2026 on the running text (decision 30)
        Assert.All(r.Chunks, c => Assert.InRange(c.TokenCount, 1, TextPieces.MaxTokens));
        var counter = Counter();
        Assert.All(r.Chunks, c => Assert.Equal(counter.Count(c.Text), c.TokenCount));
    }

    [CorpusFact]
    public void Rule3_pieces_never_overlap_and_rejoin_to_their_block_exactly()
    {
        var r = Build();
        int k = 0;
        foreach (var (g, section, text) in r.Blocks)
        {
            var first = r.Chunks[k];
            var mine = r.Chunks.Skip(k).Take(first.Pieces).ToList();
            Assert.All(mine, c => Assert.Equal((g, section), (c.SubGuideline, c.Section)));
            Assert.Equal(Enumerable.Range(1, first.Pieces), mine.Select(c => c.Piece));
            Assert.Equal(text, string.Join(" ", mine.Select(c => c.Text[(c.ContextHeader.Length + 1)..])));
            k += first.Pieces;
        }
        Assert.Equal(r.Chunks.Count, k);
    }

    [CorpusFact]
    public void Rule5_and_6_headers_hashes_and_ids()
    {
        var r = Build();
        Assert.All(r.Chunks, c =>
        {
            Assert.Equal(RecommendationChunks.Header(c.SubGuideline, c.Section), c.ContextHeader);
            Assert.StartsWith(c.ContextHeader + "\n", c.Text);
            Assert.Equal(ChunkHash.Compute(RecommendationChunks.CorpusVersion, c.ChunkId, c.Text), c.ChunkHash);
            Assert.Matches(@"^[A-Z]{3}-P\d{3}$", c.ChunkId);
        });
        Assert.Equal(r.Chunks.Count, r.Chunks.Select(c => c.ChunkId).Distinct().Count());
        Assert.Equal(r.Chunks.Select(c => c.ChunkHash), Build().Chunks.Select(c => c.ChunkHash));   // deterministic
    }

    [CorpusFact]
    public void Prose_comes_only_from_prose_sections_or_from_loose_text_in_a_body_section()
    {
        var r = Build();
        foreach (var g in fixture.Sections.SubGuidelines)
        {
            var excludedTopLevel = g.Headings.Where(h => h.Kind == "exclude").Select(h => h.Heading).ToHashSet();
            Assert.DoesNotContain(r.Chunks, c => c.SubGuideline == g.Name && excludedTopLevel.Contains(c.Section));
        }
        var reasons = fixture.Sections.SubGuidelines.SelectMany(g => g.Headings).Select(h => h.Reason).Append(ProseChunks.TitlePage).ToHashSet();
        Assert.All(r.ExcludedLines.Keys, k => Assert.Contains(k, reasons));
    }

    [CorpusFact]
    public void A_heading_the_file_does_not_list_or_an_unconfirmed_file_fails_the_build()
    {
        var unconfirmed = Clone(fixture.Sections);
        unconfirmed.Status = "proposed";
        Assert.Throws<InvalidOperationException>(() => Build(unconfirmed));

        var missing = Clone(fixture.Sections);
        missing.SubGuidelines[0].Headings.RemoveAt(3);
        Assert.Throws<InvalidOperationException>(() => Build(missing));

        var extra = Clone(fixture.Sections);
        extra.SubGuidelines[0].Headings.Add(new HeadingDto { Heading = "NOT PRINTED", Page = 1, Kind = "prose" });
        Assert.Throws<InvalidOperationException>(() => Build(extra));
    }

    [CorpusFact]
    public void Wrapped_headings_are_read_as_one_heading_everywhere()
    {
        var (doc, recs) = fixture.Get();
        var prose = Build();
        Assert.Contains(recs, c => c.Section == "AGENTS THAT STIMULATE RED CELL PRODUCTION OR PROTEIN SUPPLEMENTATION");
        Assert.Contains(recs, c => c.Section == "AUTOLOGOUS PLATELETS – WITH EXCEPTION OF THE AUTOLOGOUS LEUCOCYTE AND PLATELET PATCH");
        Assert.Contains(prose.Chunks, c => c.Section == "FURTHER CONSIDERATIONS / FUTURE RESEARCH / KEY CONTROVERSIES");
        var rats = RationaleChunks.Build(doc, fixture.Aliases, fixture.Sections, Counter());
        var all = recs.Select(c => c.Section).Concat(rats.Select(c => c.Section)).Concat(prose.Chunks.Select(c => c.Section));
        Assert.DoesNotContain(all, s => s is "SUPPLEMENTATION" or "AND PLATELET PATCH" or "CONTROVERSIES" or "PROCESS");
    }

    [CorpusFact]
    public void Numbered_and_colon_headings_name_their_sections()
    {
        var (doc, recs) = fixture.Get();
        Assert.Equal(0, recs.Count(c => c.Section == BodyEntries.SectionHeading));
        Assert.Contains(recs, c => c.SubGuideline == "IWGDF Prevention Guideline" && c.Section == "2. REGULARLY INSPECTING AND EXAMINING THE AT-RISK FOOT");

        string[] missedByStructureCheck =
        [
            "1. IDENTIFYING THE AT-RISK FOOT", "2. REGULARLY INSPECTING AND EXAMINING THE AT-RISK FOOT",
            "3. EDUCATING THE PERSON WITH DIABETES, FAMILY AND HEALTHCARE PROVIDERS",
            "4. ENSURING ROUTINE WEARING OF APPROPRIATE FOOTWEAR", "5. TREATING RISK FACTORS FOR ULCERATION",
            "6. INTEGRATED FOOT CARE", "INTERVENTION: DEBRIDEMENT", "INTERVENTION: DRESSINGS",
            "INTERVENTION: OXYGEN AND OTHER GASES", "INTERVENTION: PHYSICAL ALTERATION OF WOUND BED",
            "INTERVENTION: SKIN SUBSTITUTES", "INTERVENTION: AUTOLOGOUS PRODUCTS",
            "INTERVENTION: PHARMACOLOGICAL INTERVENTIONS", "INTERVENTION: NEGATIVE PRESSURE WOUND THERAPY (NPWT)",
        ];
        var rats = RationaleChunks.Build(doc, fixture.Aliases, fixture.Sections, Counter());
        var sections = recs.Select(c => c.Section).Concat(rats.Select(c => c.Section))
            .Concat(Build().Chunks.Select(c => c.Section)).ToHashSet();
        Assert.All(missedByStructureCheck, h => Assert.Contains(h, sections));
    }

    private static SectionsFileDto Clone(SectionsFileDto s) => new()
    {
        Status = s.Status, ConfirmedBy = s.ConfirmedBy, ConfirmedOn = s.ConfirmedOn, SourcePdfSha256 = s.SourcePdfSha256, Generated = s.Generated,
        SubGuidelines = s.SubGuidelines.Select(g => new SectionsSubGuidelineDto
        {
            Name = g.Name,
            Wraps = g.Wraps.Select(w => new WrapDto { First = w.First, Second = w.Second, Page = w.Page }).ToList(),
            Headings = g.Headings.Select(h => new HeadingDto { Heading = h.Heading, Page = h.Page, Kind = h.Kind, Reason = h.Reason }).ToList(),
        }).ToList(),
    };
}
