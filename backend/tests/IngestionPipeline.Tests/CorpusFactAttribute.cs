// Tests that need gitignored corpus files (the PDF, the vocabulary, and for tables the hand transcriptions).
// Without them (CI) they are reported as skipped; xUnit 2 has no Assert.Skip, so the attribute sets Skip.

using IngestionPipeline;
using IngestionPipeline.Corpus;
using IngestionPipeline.Commands;

namespace IngestionPipeline.Tests;

public sealed class CorpusFactAttribute : FactAttribute
{
    public CorpusFactAttribute()
    {
        var reason = CorpusFiles.MissingReason();
        if (reason is not null) Skip = reason;
    }
}

public sealed class CorpusTheoryAttribute : TheoryAttribute
{
    public CorpusTheoryAttribute()
    {
        var reason = CorpusFiles.MissingReason();
        if (reason is not null) Skip = reason;
    }
}

public sealed class TablesFactAttribute : FactAttribute
{
    public TablesFactAttribute()
    {
        var reason = CorpusFiles.TablesMissingReason();
        if (reason is not null) Skip = reason;
    }
}

public sealed class TablesTheoryAttribute : TheoryAttribute
{
    public TablesTheoryAttribute()
    {
        var reason = CorpusFiles.TablesMissingReason();
        if (reason is not null) Skip = reason;
    }
}

public sealed class LoadFactAttribute : FactAttribute
{
    public LoadFactAttribute()
    {
        var reason = CorpusFiles.LoadMissingReason();
        if (reason is not null) Skip = reason;
    }
}

public sealed class LoadDatabaseFactAttribute : FactAttribute
{
    public LoadDatabaseFactAttribute()
    {
        var reason = CorpusFiles.LoadMissingReason() ?? (TestDatabase.RagConnectionString() is null ? TestDatabase.MissingReason : null);
        if (reason is not null) Skip = reason;
    }
}

public static class CorpusFiles
{
    public static string? MissingReason()
    {
        var paths = RepoPaths.Discover();
        if (!File.Exists(paths.SourcePdf)) return $"source PDF not present at {paths.SourcePdf} (gitignored)";
        if (!File.Exists(paths.Vocabulary)) return $"vocabulary not present at {paths.Vocabulary} (gitignored)";
        return null;
    }

    /// <summary>Also needs every transcription file tables.yaml names; they are gitignored like the PDF.</summary>
    public static string? TablesMissingReason()
    {
        var reason = MissingReason();
        if (reason is not null) return reason;
        var paths = RepoPaths.Discover();
        var tables = TablesFile.Read(Path.Combine(paths.Patterns, "tables.yaml"))!;
        var files = tables.Tables.SelectMany(t => t.Transcriptions.Concat(t.Regions.SelectMany(r => r.Transcriptions)));
        foreach (var f in files)
            if (!File.Exists(Path.Combine(paths.Corpus, f.File)))
                return $"transcription not present at {f.File} (gitignored)";
        return null;
    }

    /// <summary>The load also needs the embedding cache, which `embed` writes to the gitignored .cache/.</summary>
    public static string? LoadMissingReason()
    {
        var reason = TablesMissingReason();
        if (reason is not null) return reason;
        var cache = EmbedCommand.CachePath(RepoPaths.Discover(), EmbedCommand.Documents);
        return File.Exists(cache) ? null : $"embedding cache not present at {cache} (gitignored)";
    }
}
