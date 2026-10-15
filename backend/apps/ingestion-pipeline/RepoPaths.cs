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

    /// <summary>One committed manifest per frozen corpus version; no guideline text.</summary>
    public string Manifests => Path.Combine(Corpus, "manifests");

    /// <summary>Gitignored database exports; they hold guideline text.</summary>
    public string Exports => Path.Combine(Corpus, "exports");

    /// <summary>Gitignored scratch output; may hold guideline text.</summary>
    public string Cache => Path.Combine(Corpus, ".cache");

    /// <summary>The local Phase A notes; docs/ is gitignored, so reports written here are never committed.</summary>
    public string Notes => Path.Combine(Root, "docs", "IT23294202", "guideline-corpus");

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
