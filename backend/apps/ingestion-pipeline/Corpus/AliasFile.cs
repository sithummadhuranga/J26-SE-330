// Reads and writes label_aliases.yaml, the author-confirmed list-to-body pairing (decision 2), and the
// side-by-side review copy, which quotes guideline text and so goes to .cache/.

using System.Globalization;
using System.Text;
using IngestionPipeline.Pdf;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace IngestionPipeline.Corpus;

public sealed class AliasFileDto
{
    public string Status { get; set; } = "proposed";
    public string? ConfirmedBy { get; set; }
    public string? ConfirmedOn { get; set; }
    public string SourcePdfSha256 { get; set; } = "";
    public string Generated { get; set; } = "";
    public string Method { get; set; } = "";
    public double ProposalFloor { get; set; }
    public List<SubGuidelineDto> SubGuidelines { get; set; } = [];
}

public sealed class SubGuidelineDto
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public List<PairDto> Pairs { get; set; } = [];
    public List<UnpairedDto> ListOnly { get; set; } = [];
    public List<UnpairedDto> BodyOnly { get; set; } = [];
    public List<ExcludedDto> Excluded { get; set; } = [];
}

public sealed class ExcludedDto
{
    public string Label { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class PairDto
{
    public string List { get; set; } = "";
    public string Body { get; set; } = "";
    public double Similarity { get; set; }
    public string Basis { get; set; } = "";
}

public sealed class UnpairedDto
{
    public string Label { get; set; } = "";
    public string? BestCandidate { get; set; }
    public double BestSimilarity { get; set; }
}

public static class AliasFile
{
    private static readonly Dictionary<string, string> ProposedCodes = new(StringComparer.Ordinal)
    {
        ["IWGDF Prevention Guideline"] = "PRE",
        ["IWGDF Classification Guideline"] = "CLA",
        ["IWGDF Infection Guideline"] = "INF",
        ["Intersocietal PAD Guideline"] = "PAD",
        ["IWGDF Offloading Guideline"] = "OFF",
        ["IWGDF Wound Healing Guideline"] = "WHL",
        ["IWGDF Charcot Guideline"] = "CHA",
    };

    private const string Header = """
        # label_aliases.yaml — which LIST item is which BODY recommendation.
        #
        # Written by `ingestion-pipeline -- propose-aliases` (status: proposed). The author reviews
        # every entry against .cache/label_pairing_review.md (side-by-side text, gitignored),
        # edits where needed, then sets:
        #     status: confirmed
        #     confirmed_by: <name>
        #     confirmed_on: <yyyy-mm-dd>
        # From then on the build reads ONLY this confirmed file, and propose-aliases refuses to
        # overwrite it. A pair you add by hand gets basis: author.
        #
        # Rules (decisions 1, 2, 5):
        #   * body labels are identity: chunk_id = {code}-R{body label}, e.g. OFF-R07a;
        #   * list labels are aliases; every label must resolve to exactly one recommendation;
        #   * list_only items are ingested from the list (canonical source: list);
        #   * excluded list labels are not recommendations; each carries its reason;
        #   * no guideline text belongs in this file — labels, scores and codes only.

        """;

    // Every string is double-quoted: unquoted, the code OFF reads as false in YAML 1.1 readers.
    internal static ISerializer Serializer() => new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
        .WithEventEmitter(next => new QuoteAllStrings(next))
        .Build();

    private sealed class QuoteAllStrings(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Type == typeof(string) && eventInfo.Source.Value is not null)
                eventInfo.Style = ScalarStyle.DoubleQuoted;
            base.Emit(eventInfo, emitter);
        }
    }

    // Duplicate keys throw; by default YamlDotNet silently keeps the last value.
    internal static IDeserializer Deserializer() => new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();

    public static AliasFileDto? Read(string path) =>
        File.Exists(path) ? Deserializer().Deserialize<AliasFileDto>(File.ReadAllText(path)) : null;

    public static AliasFileDto ToProposal(List<GuidelinePairing> pairing, string pdfSha256, DateOnly today) => new()
    {
        Status = "proposed",
        SourcePdfSha256 = pdfSha256,
        Generated = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Method = "Statements compared up to the grade/BPS marker as lowercase words; similarity = 2*LCS/(|a|+|b|). " +
                 "Identical word sequences paired first, then repeated mutual-best matches at or above proposal_floor.",
        ProposalFloor = LabelPairing.ProposalFloor,
        SubGuidelines = pairing.Select(g => new SubGuidelineDto
        {
            Name = g.SubGuideline,
            Code = ProposedCodes.GetValueOrDefault(g.SubGuideline, "???"),
            Pairs = g.Pairs.Select(p => new PairDto { List = p.ListLabel, Body = p.BodyLabel, Similarity = Math.Round(p.Similarity, 3), Basis = p.Basis }).ToList(),
            ListOnly = g.ListOnly.Select(u => new UnpairedDto { Label = u.Label, BestCandidate = u.BestOther, BestSimilarity = Math.Round(u.BestSimilarity, 3) }).ToList(),
            BodyOnly = g.BodyOnly.Select(u => new UnpairedDto { Label = u.Label, BestCandidate = u.BestOther, BestSimilarity = Math.Round(u.BestSimilarity, 3) }).ToList(),
        }).ToList(),
    };

    public static void Write(string path, AliasFileDto dto) =>
        File.WriteAllText(path, Header + Serializer().Serialize(dto), new UTF8Encoding(false));

    public static void WriteReview(string path, GuidelineDocument doc, List<GuidelinePairing> pairing)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# List ↔ body pairing — review copy (contains guideline text; gitignored)");
        sb.AppendLine();
        sb.AppendLine("Check each pair is the SAME recommendation. Edit patterns/label_aliases.yaml, not this file.");
        foreach (var g in pairing)
        {
            var idxs = doc.Positions(g.SubGuideline);
            var lst = doc.FindSection(idxs, "LIST OF RECOMMENDATIONS", Patterns.ListTerminators);
            var bod = doc.FindSection(idxs, "RECOMMENDATIONS", Patterns.BodyTerminators, after: lst?.Hi ?? 0);
            var listText = (lst is { } l ? doc.ParseItems(idxs, l.Lo, l.Hi) : [])
                .GroupBy(i => i.Label).ToDictionary(x => x.Key, x => x.First().Text);
            var bodyText = (bod is { } b ? doc.ParseBodyRecommendations(idxs, b.Lo, b.Hi) : [])
                .GroupBy(r => r.Label).ToDictionary(x => x.Key, x => x.First().Statement);
            string L(string? k) => k is not null && listText.TryGetValue(k, out var t) ? GuidelineDocument.StatementUptoMarker(t) ?? t : "—";
            string B(string? k) => k is not null && bodyText.TryGetValue(k, out var t) ? GuidelineDocument.StatementUptoMarker(t) ?? t : "—";

            sb.AppendLine().AppendLine($"## {g.SubGuideline}").AppendLine();
            foreach (var p in g.Pairs)
                sb.AppendLine($"### list {p.ListLabel} ↔ body {p.BodyLabel} — {p.Basis}, similarity {p.Similarity:F3}")
                  .AppendLine($"- **list:** {L(p.ListLabel)}")
                  .AppendLine($"- **body:** {B(p.BodyLabel)}").AppendLine();
            foreach (var u in g.ListOnly)
                sb.AppendLine($"### list {u.Label} — NO BODY PARTNER (best hint: body {u.BestOther ?? "—"}, {u.BestSimilarity:F3})")
                  .AppendLine($"- **list:** {L(u.Label)}")
                  .AppendLine($"- **hint body:** {B(u.BestOther)}").AppendLine();
            foreach (var u in g.BodyOnly)
                sb.AppendLine($"### body {u.Label} — NO LIST PARTNER (best hint: list {u.BestOther ?? "—"}, {u.BestSimilarity:F3})")
                  .AppendLine($"- **body:** {B(u.Label)}")
                  .AppendLine($"- **hint list:** {L(u.BestOther)}").AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
