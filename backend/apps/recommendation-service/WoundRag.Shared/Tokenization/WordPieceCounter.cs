// Counts tokens with greedy longest-match-first WordPiece over the uncased nomic-embed-text vocabulary.
// Used only for counting (A1 statistics, chunk size caps); no model ever sees its output.

using System.Globalization;
using System.Text;
using System.Text.Json;
using WoundRag.Shared.Text;

namespace WoundRag.Shared.Tokenization;

public sealed class WordPieceCounter
{
    // In this vocabulary a word-initial piece starts with U+2581, the reverse of BERT's "##".
    private const string WordStart = "▁";
    private const string Unk = "[UNK]";
    private const int MaxCharsPerWord = 100;

    /// <summary>[CLS] and [SEP]; added only when asked for.</summary>
    public const int SpecialTokenOverhead = 2;

    private readonly HashSet<string> _vocab;

    public int Size { get; }

    public WordPieceCounter(IReadOnlyCollection<string> vocab)
    {
        _vocab = new HashSet<string>(vocab, StringComparer.Ordinal);
        Size = vocab.Count;
    }

    /// <summary>Loads the vocabulary from a JSON array of strings.</summary>
    public static WordPieceCounter Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"WordPiece vocabulary not found at {path}. It is gitignored and must be placed there by hand.", path);
        var pieces = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path))
                     ?? throw new InvalidDataException($"{path} is not a JSON array of strings.");
        return new WordPieceCounter(pieces);
    }

    public int Count(string text, bool withSpecialTokens = false)
    {
        int n = 0;
        foreach (var word in BasicTokenize(text))
            n += WordPiece(word).Count;
        return withSpecialTokens ? n + SpecialTokenOverhead : n;
    }

    public List<string> Tokenize(string text)
    {
        var output = new List<string>();
        foreach (var word in BasicTokenize(text))
            output.AddRange(WordPiece(word));
        return output;
    }

    // Lowercase, strip accents, split on whitespace and punctuation. Walks code points (Rune),
    // because Python iterates a str by code point, not by UTF-16 unit.
    private static List<string> BasicTokenize(string text)
    {
        var normalised = StripAccents(text.ToLowerInvariant());
        var words = new List<string>();
        foreach (var chunk in PyText.Split(normalised))
        {
            var buf = new StringBuilder();
            foreach (var rune in chunk.EnumerateRunes())
            {
                if (IsPunct(rune))
                {
                    if (buf.Length > 0) { words.Add(buf.ToString()); buf.Clear(); }
                    words.Add(rune.ToString());
                }
                else
                {
                    buf.Append(rune.ToString());
                }
            }
            if (buf.Length > 0) words.Add(buf.ToString());
        }
        return words;
    }

    private static string StripAccents(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark)
                sb.Append(rune.ToString());
        return sb.ToString();
    }

    // BERT's rule: the ASCII ranges include symbols such as $, +, <, =, >, ^, `, |, ~.
    private static bool IsPunct(Rune rune)
    {
        int cp = rune.Value;
        if ((cp >= 33 && cp <= 47) || (cp >= 58 && cp <= 64) ||
            (cp >= 91 && cp <= 96) || (cp >= 123 && cp <= 126))
            return true;
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or
            UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or
            UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
            UnicodeCategory.OtherPunctuation => true,
            _ => false,
        };
    }

    private List<string> WordPiece(string word)
    {
        var cps = word.EnumerateRunes().Select(r => r.ToString()).ToArray();
        if (cps.Length > MaxCharsPerWord) return [Unk];
        var pieces = new List<string>();
        int start = 0;
        while (start < cps.Length)
        {
            int end = cps.Length;
            string? found = null;
            while (start < end)
            {
                var piece = string.Concat(cps[start..end]);
                var candidate = start == 0 ? WordStart + piece : piece;
                if (_vocab.Contains(candidate)) { found = candidate; break; }
                end--;
            }
            if (found is null) return [Unk];
            pieces.Add(found);
            start = end;
        }
        return pieces;
    }
}
