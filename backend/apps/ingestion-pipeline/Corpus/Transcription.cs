// Reads a hand transcription of a table (decision 4): a UTF-8 TSV grid, one line per row, a merged cell's
// text in every position it covers. Two passes must match exactly unless pass 2 was waived (decision 20).

using System.Security.Cryptography;
using System.Text;

namespace IngestionPipeline.Corpus;

public sealed record TranscribedGrid(IReadOnlyList<IReadOnlyList<string>> Rows, string Sha256)
{
    public List<TableCellBox> Cells() =>
        Rows.SelectMany((row, r) => row.Select((text, c) => new TableCellBox(c, r, c + 1, r + 1, text))).ToList();
}

public static class Transcription
{
    public static TranscribedGrid Parse(string tsv)
    {
        var lines = tsv.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (lines.Length == 0 || (lines.Length == 1 && lines[0].Length == 0))
            throw new InvalidOperationException("transcription is empty");
        var rows = lines.Select(l => (IReadOnlyList<string>)l.Split('\t').Select(TableSerializer.CellText).ToList()).ToList();
        if (rows.Select(r => r.Count).Distinct().Count() != 1)
            throw new InvalidOperationException($"rows have different numbers of cells: {string.Join(", ", rows.Select(r => r.Count).Distinct())}");
        var normalised = string.Join("\n", rows.Select(r => string.Join("\t", r)));
        return new TranscribedGrid(rows, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised))));
    }

    public static TranscribedGrid ReadAccepted(string corpusDir, string label, IReadOnlyList<TranscriptionDto> passes, bool singlePass = false)
    {
        int[] expected = singlePass ? [1] : [1, 2];
        if (passes.Select(p => p.Pass).Order().SequenceEqual(expected) is false)
            throw new InvalidOperationException($"{label}: needs exactly {(singlePass ? "pass 1 (single_pass)" : "passes 1 and 2")}");
        var grids = passes.OrderBy(p => p.Pass).Select(p =>
        {
            var path = Path.Combine(corpusDir, p.File);
            if (!File.Exists(path)) throw new InvalidOperationException($"{label}: pass {p.Pass} file not found ({p.File})");
            if (string.IsNullOrWhiteSpace(p.Sha256) || string.IsNullOrWhiteSpace(p.By) || string.IsNullOrWhiteSpace(p.On))
                throw new InvalidOperationException($"{label}: pass {p.Pass} has no recorded sha256, transcriber or date");
            var g = Parse(File.ReadAllText(path, Encoding.UTF8));
            if (g.Sha256 != p.Sha256) throw new InvalidOperationException($"{label}: pass {p.Pass} changed since its hash was recorded");
            return g;
        }).ToList();
        if (grids.Count == 2 && grids[0].Sha256 != grids[1].Sha256)
            throw new InvalidOperationException($"{label}: the two passes differ; reconcile them against the image (decision 4)");
        return grids[0];
    }
}
