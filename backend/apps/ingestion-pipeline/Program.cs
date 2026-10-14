// Phase A ingestion pipeline (Member 3): builds the rag corpus from the IWGDF 2023 PDF, one command per step.
// "Decision N" in comments refers to the numbered decision records of the WoundRAG demo repository.

using IngestionPipeline;
using IngestionPipeline.Corpus;
using IngestionPipeline.Pdf;
using UglyToad.PdfPig;

var paths = RepoPaths.Discover();
var command = args.Length > 0 ? args[0] : "";

switch (command)
{
    // Prints guideline text after the parity table; filter the output before sharing it.
    case "check-structure":
        return IngestionPipeline.Steps.StructureCheckCommand.Run(paths, args[1..]);

    case "propose-aliases":
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

    case "propose-sections":
        return IngestionPipeline.Steps.ChunkCommand.ProposeSections(paths);

    case "propose-tables":
        return IngestionPipeline.Steps.ChunkCommand.ProposeTables(paths);

    // Prints only the hash and the grid's shape, never the transcribed text.
    case "hash-transcription":
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("usage: ingestion-pipeline hash-transcription <file.tsv>");
            return 2;
        }
        var grid = Transcription.Parse(File.ReadAllText(args[1], System.Text.Encoding.UTF8));
        Console.WriteLine($"{grid.Sha256}  {grid.Rows.Count} rows x {grid.Rows[0].Count} cells");
        return 0;
    }

    case "chunk":
        return IngestionPipeline.Steps.ChunkCommand.Chunk(paths);

    case "annotate":
        return IngestionPipeline.Steps.AnnotateCommand.Annotate(paths);

    case "propose-figures":
        return IngestionPipeline.Steps.FiguresCommand.ProposeFigures(paths);

    case "divergence-report":
        return IngestionPipeline.Steps.DivergenceReportCommand.Write(paths);

    default:
        Console.Error.WriteLine("usage: ingestion-pipeline <check-structure | propose-aliases | propose-sections | propose-tables | hash-transcription FILE | chunk | annotate | propose-figures | divergence-report>");
        return 2;
}
