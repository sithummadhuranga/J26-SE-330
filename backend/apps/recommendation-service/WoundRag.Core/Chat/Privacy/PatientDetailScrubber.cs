// Removes identifiers from a question before it is stored or sent to any model (demo decision 48, option a). This
// narrows the "no patient data to any API" rule for the chat only: the scrubbed text is the only form that ever leaves the service.
// Known limit: a name without a title is not caught; the page warns, and a test keeps the limit visible.
// Short numbers (2 cm, 6 weeks, HbA1c 8.5) are clinical, not identifying, and are kept.

using System.Text.RegularExpressions;

namespace WoundRag.Core.Chat.Privacy;

// Removes identifiers from free text.
public static class PatientDetailScrubber
{
    public const string Name = "name";
    public const string Identifier = "identifier";
    public const string Email = "email";
    public const string Date = "date";
    public const string Number = "number";

    public static readonly IReadOnlyList<string> Kinds = [Name, Identifier, Email, Date, Number];

    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private const string Months = "(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\\.?";

    // Order matters: labelled numbers take their label with them, and dates go before the digit rule.
    private static readonly (string Kind, string Note, Regex Pattern)[] Rules =
    [
        // Mr, Mrs, Dr and Rev in any case, with the name after them in any case: phones drop capitals ("Mr perera"
        // reached the model in the live test). A second name word is taken only when it is capitalised.
        (Name, "[name removed]", new Regex(@"\b(?i:mrs|mr|dr|rev)\.?\s+[\p{L}'-]+(?:\s+[A-Z][\p{L}'-]*)?", Options)),
        // Ms, Miss and Master are also ordinary words (MS, "miss a dose"), so they keep needing capitals.
        (Name, "[name removed]", new Regex(@"\b(?:Ms|Miss|Master)\.?\s+[A-Z][\p{L}'-]*(?:\s+[A-Z][\p{L}'-]*)?", Options)),
        // Sinhala titles follow the name (මහතා, මහත්මිය, මෙනවිය); a case ending such as ට is kept. No \b:
        // Sinhala vowel signs are not word characters.
        (Name, "[name removed]", new Regex(@"[඀-෿]+\s+(?:මහත්මිය|මහතා|මෙනවිය)", Options)),
        // Tamil titles come first (திருமதி, திரு, செல்வி); the longer one first because திரு is its prefix.
        (Name, "[name removed]", new Regex(@"(?<![஀-௿])(?:திருமதி|திரு|செல்வி)\s+[஀-௿]+", Options)),
        // A label and a value containing a digit ("ID card" is not an identifier).
        (Identifier, "[ID removed]", new Regex(@"\b(?:MRN|BHT|NIC|ID|Reg(?:istration)?\s*No|Clinic\s*No)\.?\s*[:#]?\s*(?=[A-Za-z0-9/-]*\d)[A-Za-z0-9/-]{3,}", Options)),
        // Sri Lankan NIC: 9 digits + V or X, or 12 digits starting 19 or 20.
        (Identifier, "[ID removed]", new Regex(@"\b(?:\d{9}[VvXx]|(?:19|20)\d{10})\b", Options)),
        (Email, "[email removed]", new Regex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", Options)),
        (Date, "[date removed]", new Regex(@"\b\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}\b", Options)),
        (Date, "[date removed]", new Regex(@"\b\d{4}-\d{1,2}-\d{1,2}\b", Options)),
        (Date, "[date removed]", new Regex($@"\b\d{{1,2}}\s+{Months},?\s+\d{{4}}\b", Options)),
        (Date, "[date removed]", new Regex($@"\b{Months}\s+\d{{1,2}},?\s+\d{{4}}\b", Options)),
        // 7 or more digits, with an optional + and single spaces or dashes between them.
        (Number, "[number removed]", new Regex(@"(?<![\w.])\+?\d(?:[\s-]?\d){6,}(?![\w.])", Options)),
    ];

    public static ScrubResult Scrub(string text)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var result = text;
        foreach (var (kind, note, pattern) in Rules)
        {
            var replaced = pattern.Replace(result, note);
            if (replaced != result) removed.Add(kind);
            result = replaced;
        }
        return new ScrubResult(result, [.. Kinds.Where(removed.Contains)]);
    }
}
