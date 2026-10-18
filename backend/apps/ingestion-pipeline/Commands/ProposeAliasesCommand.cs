// `propose-aliases` (plan step A2): pairs list and body labels and proposes label_aliases.yaml; refuses once it is
// confirmed. The review copy with both wordings goes only to the gitignored .cache/.

using IngestionPipeline.Corpus;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;

namespace IngestionPipeline.Commands;

public sealed class ProposeAliasesCommand(RepoPaths paths) : ICommand
{
    public string Name => "propose-aliases";

    public string Usage => "propose-aliases";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Run());

    private int Run()
    {
        var aliasPath = Path.Combine(paths.Patterns, "label_aliases.yaml");
        if (AliasFile.Read(aliasPath)?.Status == "confirmed")
        {
            Console.Error.WriteLine($"{Path.GetRelativePath(paths.Root, aliasPath)} is confirmed; refusing to overwrite it.");
            return 1;
        }
        string sha;
        using (var fs = File.OpenRead(paths.SourcePdf))
            sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
        using var pdf = PdfDocument.Open(paths.SourcePdf);
        var doc = GuidelineDocument.Load(pdf);
        var pairing = LabelPairing.Propose(doc);
        AliasFile.Write(aliasPath, AliasFile.ToProposal(pairing, sha, DateOnly.FromDateTime(DateTime.Now)));
        var reviewPath = Path.Combine(paths.Cache, "label_pairing_review.md");
        AliasFile.WriteReview(reviewPath, doc, pairing);

        Console.WriteLine($"{"sub-guideline",-32} {"identical",9} {"similar",8} {"list-only",10} {"body-only",10}");
        foreach (var g in pairing)
            Console.WriteLine($"{g.SubGuideline,-32} {g.Pairs.Count(p => p.Basis == "identical"),9} " +
                              $"{g.Pairs.Count(p => p.Basis == "similar"),8} {g.ListOnly.Count,10} {g.BodyOnly.Count,10}");
        Console.WriteLine($"{"TOTAL",-32} {pairing.Sum(g => g.Pairs.Count(p => p.Basis == "identical")),9} " +
                          $"{pairing.Sum(g => g.Pairs.Count(p => p.Basis == "similar")),8} " +
                          $"{pairing.Sum(g => g.ListOnly.Count),10} {pairing.Sum(g => g.BodyOnly.Count),10}");
        Console.WriteLine($"wrote {Path.GetRelativePath(paths.Root, aliasPath)} (status: proposed)");
        Console.WriteLine($"wrote {Path.GetRelativePath(paths.Root, reviewPath)} (review copy, gitignored)");
        return 0;
    }
}
