// The manifest built from made-up facts: no database, no data file.

using System.Text;
using System.Text.Json.Nodes;
using IngestionPipeline.Db;
using IngestionPipeline.ModelApi;

namespace IngestionPipeline.Tests;

public class CorpusManifestTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static ManifestFacts Facts() => new(
        new ManifestColumns { Pdf = new string('a', 64), Chunker = "git:abc", Embedding = "emb", Dims = 256, Reranker = "rr", Instruct = new string('b', 64), K = 20 },
        [("guideline_chunks", 3), ("rerank_scores", 40)],
        [("RECOMMENDATION", 2), ("TABLE", 1)],
        (2, 1, 1),
        [("queries.yaml", new string('c', 64))]);

    [Fact]
    public void The_facts_go_where_the_manifest_names_them()
    {
        var m = CorpusManifest.Build("test-1.r1", Facts(), "git:def", At);
        Assert.Equal("test-1.r1", (string?)m["corpus_version"]);
        Assert.Equal("2026-10-14T12:00:00.0000000+00:00", (string?)m["frozen_at"]);
        Assert.Equal("git:abc", (string?)m["chunker_version"]);
        Assert.Equal("git:def", (string?)m["verified_at_commit"]);
        Assert.Equal(RerankInstruction.Text, (string?)m["reranker"]!["instruction"]);
        Assert.Equal(40, (long)m["rows"]!["rerank_scores"]!);
        Assert.Equal((2, 1, 1), ((long)m["recommendation_count"]!["total"]!, (long)m["recommendation_count"]!["grade"]!, (long)m["recommendation_count"]!["best_practice"]!));
        Assert.Equal(new string('c', 64), (string?)m["pattern_files_sha256"]!["queries.yaml"]);
    }

    [Fact]
    public void The_file_is_utf8_without_a_bom_ends_in_a_newline_and_keeps_plus_readable()
    {
        var bytes = CorpusManifest.Serialize(CorpusManifest.Build("test-1.r1", Facts(), "git:def", At));
        Assert.NotEqual(0xEF, bytes[0]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.EndsWith("}\n", text);
        Assert.Contains("+00:00", text);
        Assert.DoesNotContain("\\u002B", text);
        Assert.Equal("test-1.r1", (string?)JsonNode.Parse(text)!["corpus_version"]);
    }
}
