// `propose-sections` (plan step A2): proposes sections.yaml from the PDF headings; refuses once it is confirmed.

using IngestionPipeline.Corpus;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;

namespace IngestionPipeline.Commands;

public sealed class ProposeSectionsCommand(RepoPaths paths) : ICommand
{
    public string Name => "propose-sections";

    public string Usage => "propose-sections";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Run());

    private static string AliasPath(RepoPaths p) => Path.Combine(p.Patterns, "label_aliases.yaml");

    private static string SectionsPath(RepoPaths p) => Path.Combine(p.Patterns, "sections.yaml");

    private int Run()
    {
        var path = SectionsPath(paths);
        if (SectionsFile.Read(path)?.Status == "confirmed")
        {
            Console.Error.WriteLine($"{Path.GetRelativePath(paths.Root, path)} is confirmed; refusing to overwrite it.");
            return 1;
        }
        var aliases = AliasFile.Read(AliasPath(paths)) ?? throw new FileNotFoundException(AliasPath(paths));
        string sha;
        using (var fs = File.OpenRead(paths.SourcePdf))
            sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        var doc = GuidelineDocument.Load(pdf);
        var dto = SectionsFile.Propose(doc, aliases.SubGuidelines.Select(g => g.Name), sha, DateOnly.FromDateTime(DateTime.Now));
        SectionsFile.Write(path, dto);

        foreach (var g in dto.SubGuidelines)
            Console.WriteLine($"{g.Name,-32} headings {g.Headings.Count,3}: prose {g.Headings.Count(h => h.Kind == "prose"),2}, " +
                              $"exclude {g.Headings.Count(h => h.Kind == "exclude"),2}, sub {g.Headings.Count(h => h.Kind == "sub"),2}; wraps {g.Wraps.Count}");
        Console.WriteLine($"wrote {Path.GetRelativePath(paths.Root, path)} (status: proposed)");
        return 0;
    }
}
