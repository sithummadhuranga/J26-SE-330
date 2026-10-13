// Phase A ingestion pipeline (Member 3): builds the rag corpus from the IWGDF 2023 PDF, one command per step.
// "Decision N" in comments refers to the numbered decision records of the WoundRAG demo repository.

using IngestionPipeline;

var paths = RepoPaths.Discover();
var command = args.Length > 0 ? args[0] : "";

switch (command)
{
    // Prints guideline text after the parity table; filter the output before sharing it.
    case "a1":
        return IngestionPipeline.Steps.A1Command.Run(paths, args[1..]);

    default:
        Console.Error.WriteLine("usage: ingestion-pipeline <a1>");
        return 2;
}
