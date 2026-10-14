// Whitespace split, join and strip that behave exactly like Python's str.split() and str.strip(),
// so text normalises the same way it did when the structure numbers were measured.

namespace WoundRag.Shared.Text;

public static class WhitespaceText
{
    // Python also treats U+001C..U+001F as whitespace; .NET's char.IsWhiteSpace does not.
    public static bool IsWhiteSpace(char c) =>
        char.IsWhiteSpace(c) || (c >= '\u001C' && c <= '\u001F');

    public static List<string> Split(string text)
    {
        var words = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && IsWhiteSpace(text[i])) i++;
            int start = i;
            while (i < text.Length && !IsWhiteSpace(text[i])) i++;
            if (i > start) words.Add(text[start..i]);
        }
        return words;
    }

    /// <summary>Python's " ".join(text.split()).</summary>
    public static string NormalizeSpaces(string text) => string.Join(' ', Split(text));

    public static string Strip(string text)
    {
        int lo = 0, hi = text.Length;
        while (lo < hi && IsWhiteSpace(text[lo])) lo++;
        while (hi > lo && IsWhiteSpace(text[hi - 1])) hi--;
        return text[lo..hi];
    }

    public static string RStrip(string text, string chars)
    {
        int hi = text.Length;
        while (hi > 0 && chars.Contains(text[hi - 1])) hi--;
        return text[..hi];
    }
}
