// Which response section each RECOMMENDATION and TABLE goes in: a file the author confirms (decision 37), never a
// model's choice. Sha256 is of the file's bytes, recorded in every response's audit.

namespace WoundRag.Core.Composition;

public sealed record SectionPlacement(string Status, string CorpusVersion, IReadOnlyDictionary<string, string> Sections, string Sha256)
{
    public const string Confirmed = "confirmed";

    public string? WhyNotUsable(string servedVersion) =>
        Status != Confirmed ? "the response sections file is not confirmed by the author"
        : CorpusVersion != servedVersion ? $"the response sections file is for {CorpusVersion}, not {servedVersion}"
        : null;
}
