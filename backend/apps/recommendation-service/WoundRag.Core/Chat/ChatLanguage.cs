// The chat's three languages (demo decision 47). Detection is by Unicode block, not by a model: deterministic and free.
// Known limit: Sinhala or Tamil typed in Latin letters reads as English; the clinician's explicit choice always wins.
// On "auto" a request in the question itself ("in Sinhala", සිංහලෙන්, தமிழில்) is honoured, and the model never sees it.

using System.Text.RegularExpressions;

namespace WoundRag.Core.Chat;

// Language codes and the rules that pick one.
public static class ChatLanguage
{
    public const string English = "en";
    public const string Sinhala = "si";
    public const string Tamil = "ta";
    public const string Auto = "auto";

    // Sinhala U+0D80–U+0DFF, Tamil U+0B80–U+0BFF. Their vowel signs are combining marks, not letters, so the
    // whole block is counted.
    private static bool IsSinhala(char c) => c is >= '඀' and <= '෿';
    private static bool IsTamil(char c) => c is >= '஀' and <= '௿';
    private static bool IsLatinLetter(char c) => char.IsLetter(c) && c < 'ɐ';

    public static string Detect(string text)
    {
        var sinhala = text.Count(IsSinhala);
        var tamil = text.Count(IsTamil);
        var latin = text.Count(IsLatinLetter);
        if (sinhala > latin && sinhala > tamil) return Sinhala;
        if (tamil > latin && tamil > sinhala) return Tamil;
        return English;
    }

    // "tell me in sinhala pls", "answer in Tamil please:"; only with "in" or "into", so "Sinhala patients" is not one.
    private static readonly Regex EnglishRequest = new(
        @"[\s,.;:]*(?:(?:please|pls|plz)\s+)?(?:(?:tell|answer|explain|reply|say|write)(?:\s+(?:it|me|this|that))*\s+)?\b(?:in|into)\s+(sinhala|sinhalese|tamil)\b(?:\s+language)?(?:\s+(?:please|pls|plz))?[\s,.;:]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A language asked for in the question, or null.
    public static string? Requested(string question)
    {
        if (question.Contains("සිංහලෙන්", StringComparison.Ordinal)) return Sinhala;
        if (question.Contains("தமிழில்", StringComparison.Ordinal)) return Tamil;
        var m = EnglishRequest.Match(question);
        return !m.Success ? null : m.Groups[1].Value.StartsWith("t", StringComparison.OrdinalIgnoreCase) ? Tamil : Sinhala;
    }

    // The question without an English-letter language request; unchanged if nothing else would be left.
    public static string WithoutRequest(string question)
    {
        var stripped = EnglishRequest.Replace(question, " ").Trim();
        return stripped.Length == 0 ? question : stripped;
    }

    public static string Resolve(string choice, string question) => choice switch
    {
        Auto => Requested(question) ?? Detect(question),
        English or Sinhala or Tamil => choice,
        _ => throw new ArgumentException($"unknown answer language '{choice}'", nameof(choice)),
    };

    public static bool IsLatinOnly(string query) =>
        !query.Any(c => IsSinhala(c) || IsTamil(c) || (char.IsLetter(c) && !IsLatinLetter(c)));
}
