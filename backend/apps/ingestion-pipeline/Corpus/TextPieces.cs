// Cuts a RATIONALE or PROSE block into 250–400-token pieces at sentence ends (decision 13): fewest pieces
// over the cap, then fewest under it, then the most even sizes, found exactly by dynamic programming.
// Greedy filling was tried first and left 53 short pieces.

using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Corpus;

public sealed record TextPiece(int Offset, string Text, int Tokens);

public static class TextPieces
{
    public const int MaxTokens = 400;

    public const int MinTokens = 250;

    private const int Target = (MinTokens + MaxTokens) / 2;

    public static List<TextPiece> Cut(string block, string header, WordPieceCounter counter)
    {
        if (block.Length == 0) throw new InvalidOperationException("empty block cannot be chunked");
        var s = Sentences.Split(block);
        int n = s.Count;
        int End(int k) => s[k].Offset + s[k].Text.Length;
        int Count(int i, int j) => counter.Count(header + "\n" + block[s[i].Offset..End(j)]);   // sentences i..j inclusive

        var best = new (int Over, int Under, int Pieces, long Spread)[n + 1];
        var from = new int[n + 1];
        for (int j = 1; j <= n; j++)
        {
            best[j] = (int.MaxValue, 0, 0, 0);
            for (int i = j - 1; i >= 0; i--)
            {
                int t = Count(i, j - 1);
                if (t > MaxTokens && i < j - 1) break;   // token counts only grow as i moves left
                bool wholeBlock = i == 0 && j == n;
                var prev = best[i];
                var cand = (prev.Over + (t > MaxTokens ? 1 : 0),
                            prev.Under + (t < MinTokens && !wholeBlock ? 1 : 0),
                            prev.Pieces + 1,
                            prev.Spread + (long)(t - Target) * (t - Target));
                if (cand.CompareTo(best[j]) < 0) { best[j] = cand; from[j] = i; }
            }
        }

        var cuts = new List<(int I, int J)>();
        for (int j = n; j > 0; j = from[j]) cuts.Add((from[j], j - 1));
        cuts.Reverse();
        return cuts.Select(c => new TextPiece(s[c.I].Offset, block[s[c.I].Offset..End(c.J)], Count(c.I, c.J))).ToList();
    }
}
