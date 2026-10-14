// Finds the repository root by walking up to global.json, since `dotnet run` and `dotnet test`
// start in different folders. The corpus lives under research/IT23294202/guideline-corpus/.

namespace IngestionPipeline;

public sealed class RepoPaths
{
    public string Root { get; }

    public string Corpus => Path.Combine(Root, "research", "IT23294202", "guideline-corpus");

    /// <summary>The author-confirmed pattern files; committed, they hold IDs and settings only.</summary>
    public string Patterns => Path.Combine(Corpus, "patterns");

    public string SourcePdf =>Path.Combine(Corpus, "source", "IWGDF-Guidelines-2023.pdf");

    public string Vocabulary => Path.Combine(Cache, "vocab-nomic-embed-text.json");

    /// <summary>Gitignored scratch output; may hold guideline text.</summary>
    public string Cache => Path.Combine(Corpus, ".cache");

    private RepoPaths(string root) => Root = root;

    public static RepoPaths Discover(string? start = null)
    {
        foreach (var origin in new[] { start, Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = origin is null ? null : new DirectoryInfo(origin); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "global.json")))
                    return new RepoPaths(dir.FullName);
        }
        throw new DirectoryNotFoundException("Repository root not found: no global.json above the working directory.");
    }
}
