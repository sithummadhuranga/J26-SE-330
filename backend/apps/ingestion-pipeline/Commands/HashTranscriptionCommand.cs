// `hash-transcription FILE`: prints a hand transcription's hash and grid shape, never the transcribed text.

using IngestionPipeline.Corpus;

namespace IngestionPipeline.Commands;

public sealed class HashTranscriptionCommand : ICommand
{
    public string Name => "hash-transcription";

    public string Usage => "hash-transcription FILE";

    public Task<int> RunAsync(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: ingestion-pipeline hash-transcription <file.tsv>");
            return Task.FromResult(2);
        }
        var grid = Transcription.Parse(File.ReadAllText(args[0], System.Text.Encoding.UTF8));
        Console.WriteLine($"{grid.Sha256}  {grid.Rows.Count} rows x {grid.Rows[0].Count} cells");
        return Task.FromResult(0);
    }
}
