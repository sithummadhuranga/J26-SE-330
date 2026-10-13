// A test that needs the gitignored PDF and vocabulary; without them (CI) it is reported as skipped.
// xUnit 2 has no Assert.Skip, so the attribute sets Skip when it is constructed.

using IngestionPipeline;

namespace IngestionPipeline.Tests;

public sealed class CorpusFactAttribute : FactAttribute
{
    public CorpusFactAttribute()
    {
        var reason = CorpusFiles.MissingReason();
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
}
