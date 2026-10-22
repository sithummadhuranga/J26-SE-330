// The export script assembled from a made-up pg_dump output: no database, no data file.

using IngestionPipeline.Db;

namespace IngestionPipeline.Tests;

public class CorpusExportTests
{
    private static readonly string Sha = new('a', 64);

    private static string Dump(params string[] versionRows) => string.Join('\n', [
        "--", "-- PostgreSQL database dump", "--", "", "\\restrict KEY", "",
        "SELECT pg_catalog.set_config('search_path', '', false);", "",
        "COPY rag.corpus_versions (corpus_version, manifest_sha256, frozen_at) FROM stdin;", .. versionRows, "\\.", "",
        "COPY rag.guideline_chunks (corpus_version, chunk_id) FROM stdin;", "test-1.r1\tC1", "\\.", "",
        "--", "-- PostgreSQL database dump complete", "--", "", "\\unrestrict KEY", ""]);

    private static readonly string OneVersion = Dump("test-1.r1\t" + Sha + "\t2026-10-15 05:00:00+00");

    [Fact]
    public void The_version_row_goes_in_unfrozen_and_is_frozen_after_the_other_tables()
    {
        var lines = CorpusExport.Assemble(OneVersion, "test-1.r1", Sha).Split('\n').ToList();
        var begin = lines.IndexOf("BEGIN;");
        var held = lines.FindIndex(l => l.StartsWith("COPY pg_temp.corpus_version_dumped (corpus_version, manifest_sha256, frozen_at) FROM stdin;"));
        var insert = lines.IndexOf("INSERT INTO rag.corpus_versions SELECT * FROM pg_temp.corpus_version_open;");
        var chunks = lines.FindIndex(l => l.StartsWith("COPY rag.guideline_chunks "));
        var freeze = lines.FindIndex(l => l.StartsWith("UPDATE rag.corpus_versions v SET manifest_sha256"));
        var commit = lines.IndexOf("COMMIT;");
        Assert.True(lines.IndexOf("\\restrict KEY") < begin && begin < held && held < insert && insert < chunks && chunks < freeze && freeze < commit);
        Assert.True(commit < lines.IndexOf("-- PostgreSQL database dump complete") && lines.IndexOf("\\unrestrict KEY") > commit);
        Assert.DoesNotContain(lines, l => l.StartsWith("COPY rag.corpus_versions"));
        Assert.Equal(new[] { "test-1.r1\t" + Sha + "\t2026-10-15 05:00:00+00", "test-1.r1\tC1" }, lines.Where(l => l.StartsWith("test-1.r1\t")));
        Assert.Contains(lines, l => l.Contains($"corpus_version = 'test-1.r1' AND frozen_at IS NOT NULL AND manifest_sha256 = '{Sha}'"));
    }

    [Fact]
    public void A_dump_with_another_version_row_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => CorpusExport.Assemble(
            Dump("test-1.r1\t" + Sha + "\t2026-10-15 05:00:00+00", "test-0.r1\t" + Sha + "\t2026-10-01 05:00:00+00"), "test-1.r1", Sha));
        Assert.Throws<InvalidDataException>(() => CorpusExport.Assemble(OneVersion, "test-2.r1", Sha));
    }

    [Fact]
    public void A_dump_whose_first_data_is_not_the_version_table_is_refused()
    {
        var swapped = OneVersion.Replace("COPY rag.corpus_versions", "COPY rag.figures");
        Assert.Throws<InvalidDataException>(() => CorpusExport.Assemble(swapped, "test-1.r1", Sha));
        Assert.Throws<InvalidDataException>(() => CorpusExport.Assemble(OneVersion.Replace("-- PostgreSQL database dump complete", "--"), "test-1.r1", Sha));
        Assert.Throws<InvalidDataException>(() => CorpusExport.Assemble(OneVersion, "test-1.r1", "not-a-hash"));
    }
}
