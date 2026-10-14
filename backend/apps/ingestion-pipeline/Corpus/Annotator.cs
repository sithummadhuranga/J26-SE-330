// Plan step A4: for each RECOMMENDATION chunk, finds the population clause (variants A and C), the fields
// that gate it (Req(d)), the fields only mentioned, the polarity and the runtime thresholds. Every pattern
// comes from annotation.yaml; a field gates only when one of its terms is printed inside a clause.

using System.Text.RegularExpressions;
using IngestionPipeline.Pdf;

namespace IngestionPipeline.Corpus;

public sealed class AnnotationFileDto
{
    public string Status { get; set; } = "proposed";
    public string? ConfirmedBy { get; set; }
    public string? ConfirmedOn { get; set; }
    public string? PrimaryVariant { get; set; }
    public List<string> VariantAOpeners { get; set; } = [];
    public string VariantCStart { get; set; } = "";
    public ClauseEndDto ClauseEnd { get; set; } = new();
    public Dictionary<string, List<string>> FieldTerms { get; set; } = [];
    public Dictionary<string, List<string>> AuthorPrerequisites { get; set; } = [];
    public List<string> ProhibitMainClause { get; set; } = [];
    public string ProhibitAnywhere { get; set; } = "";
    public string NegationWatch { get; set; } = "";
    public string QualifierWatch { get; set; } = "";
    public List<ThresholdDto> Thresholds { get; set; } = [];
}

public sealed class ClauseEndDto
{
    public List<string> MainClauseWords { get; set; } = [];
    public bool AdverbAfterComma { get; set; }
    public string SubjectModal { get; set; } = "";
    public string Modal { get; set; } = "";
    public string Infinitive { get; set; } = "";
    public List<string> Words { get; set; } = [];
}

public sealed class ThresholdDto
{
    public string ThresholdId { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public string Token { get; set; } = "";
    public decimal Value { get; set; }
    public string Unit { get; set; } = "";
}

public sealed record Clause(int Start, int End, string EndedBy);

public sealed record ChunkAnnotation(
    string ChunkId, Clause? ClauseA, List<Clause> ClausesC,
    List<string> ReqA, List<string> ReqC, List<string> ReqAuthor, List<string> MentionedA, List<string> MentionedC,
    string Polarity, List<string> Flags);

public sealed record LocatedThreshold(string ThresholdId, decimal Value, string Unit, string QuotedSource, int PdfIndex, string ChunkId);

public sealed class Annotator
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private readonly List<Regex> _openers;
    private readonly Regex _cStart;
    private readonly HashSet<string> _mainWords;
    private readonly bool _adverb;
    private readonly Regex _subjectModal;
    private readonly Regex _modal;
    private readonly Regex _infinitive;
    private readonly Regex? _words;
    private readonly Regex _qualifier;
    private readonly List<(string Field, Regex Term)> _terms;
    private readonly List<Regex> _prohibit;
    private readonly Regex _negation;
    private readonly Regex _prohibitAnywhere;
    private readonly Dictionary<string, List<string>> _authorPrerequisites;

// "•" counts as a word: PAD-R23–R25 list their main clauses as bullets after a colon.
    private static readonly Regex AfterPunct = new(@"[,:]\s*(•|[\w'-]+)", RegexOptions.CultureInvariant);

    public Annotator(AnnotationFileDto file)
    {
        _openers = file.VariantAOpeners.Select(p => new Regex(p, Ci)).ToList();
        _cStart = new Regex(file.VariantCStart, Ci);
        _mainWords = new HashSet<string>(file.ClauseEnd.MainClauseWords, StringComparer.OrdinalIgnoreCase);
        _adverb = file.ClauseEnd.AdverbAfterComma;
        _subjectModal = new Regex(file.ClauseEnd.SubjectModal, Ci);
        _modal = new Regex(file.ClauseEnd.Modal, Ci);
        _infinitive = new Regex(file.ClauseEnd.Infinitive, Ci);
        _words = file.ClauseEnd.Words.Count == 0 ? null
            : new Regex(@"\s(?:" + string.Join("|", file.ClauseEnd.Words.Select(Regex.Escape)) + @")\b", Ci);
        _qualifier = new Regex(file.QualifierWatch, Ci);
        if (file.PrimaryVariant is not null && !Variants.Contains(file.PrimaryVariant))
            throw new InvalidDataException($"annotation.yaml: primary_variant '{file.PrimaryVariant}' is not A, C or union.");
        foreach (var f in file.FieldTerms.Keys)
            if (!Vocabulary.Contains(f))
                throw new InvalidDataException($"annotation.yaml: field '{f}' is not in the decision 7 vocabulary.");
        foreach (var (id, fields) in file.AuthorPrerequisites)
        {
            var unknown = fields.FirstOrDefault(f => !Vocabulary.Contains(f));
            if (unknown is not null)
                throw new InvalidDataException($"annotation.yaml: author_prerequisites {id}: field '{unknown}' is not in the decision 7 vocabulary.");
        }
        _authorPrerequisites = file.AuthorPrerequisites;
        _terms = file.FieldTerms.SelectMany(kv => kv.Value.Select(t => (kv.Key, new Regex(t, Ci)))).ToList();
        _prohibit = file.ProhibitMainClause.Select(p => new Regex(p, Ci)).ToList();
        _negation = new Regex(file.NegationWatch, Ci);
        _prohibitAnywhere = new Regex(file.ProhibitAnywhere, Ci);
    }

// The decision 7 vocabulary, the same list as the CHECK in rag/0003.
    public static readonly IReadOnlySet<string> Vocabulary = new HashSet<string>(StringComparer.Ordinal)
        { "ulcerLocation", "protectiveSensation", "pedalPulses", "probeToBone", "infectionGrade", "healingTrajectory" };

    public static readonly IReadOnlySet<string> Variants = new HashSet<string>(StringComparer.Ordinal) { "A", "C", "union" };

    public static List<string> Required(ChunkAnnotation n, string variant)
    {
        var found = variant switch
        {
            "A" => n.ReqA,
            "C" => n.ReqC,
            "union" => Merged(n.ReqA, n.ReqC),
            _ => throw new ArgumentException($"primary_variant '{variant}' is not A, C or union.", nameof(variant)),
        };
        return Merged(found, n.ReqAuthor);
    }

    public static List<string> Mentioned(ChunkAnnotation n, string variant)
    {
        var required = Required(n, variant);
        var named = Merged(Merged(n.MentionedA, n.MentionedC), Merged(n.ReqA, n.ReqC));
        return [.. named.Where(f => !required.Contains(f))];
    }

    private static List<string> Merged(List<string> a, List<string> b) => [.. a.Union(b).Order(StringComparer.Ordinal)];

// Drops the context header and the GRADE / best-practice marker, cut at its "(" only when one directly
// precedes it: 24 statements follow a full stop with no parenthesis, so an earlier "(" is real text.
    public static string BareStatement(string chunkText)
    {
        var nl = chunkText.IndexOf('\n');
        var s = nl < 0 ? chunkText : chunkText[(nl + 1)..];
        var marks = Patterns.GradeMarker.Matches(s).Concat(Patterns.Bps.Matches(s)).Select(m => m.Index).ToList();
        if (marks.Count == 0) return s.Trim();
        var cut = marks.Min();
        var before = s[..cut].TrimEnd();
        if (before.EndsWith('(')) cut = before.Length - 1;
        return s[..cut].Trim();
    }

    public Clause? ClauseA(string s) => _openers.Any(o => o.IsMatch(s)) ? ClauseFrom(s, 0) : null;

    public List<Clause> ClausesC(string s)
    {
        var result = new List<Clause>();
        var from = 0;
        while (from < s.Length)
        {
            var m = _cStart.Match(s, from);
            if (!m.Success) break;
            var c = ClauseFrom(s, m.Index);
            result.Add(c);
            from = Math.Max(c.End, m.Index + m.Length);
        }
        return result;
    }

    public Clause ClauseFrom(string s, int start)
    {
        var ends = new List<(int At, string By)>();
        foreach (Match m in AfterPunct.Matches(s, start))
        {
            var w = m.Groups[1].Value;
            if (_mainWords.Contains(w)) { ends.Add((m.Index, "main-clause word")); break; }
            if (_adverb && s[m.Index] == ',' && w.Length > 2 && w.EndsWith("ly", StringComparison.OrdinalIgnoreCase))
            { ends.Add((m.Index, "adverb after comma")); break; }
        }
        var sm = _subjectModal.Match(s, start);
        if (sm.Success) ends.Add((sm.Index, "subject and modal"));
        var md = _modal.Match(s, start);
        if (md.Success) ends.Add((md.Index, "modal"));
        var inf = _infinitive.Match(s, start);
        if (inf.Success) ends.Add((inf.Index, "infinitive"));
        var wd = _words?.Match(s, start);
        if (wd is { Success: true }) ends.Add((wd.Index, "frequency word"));
        if (ends.Count == 0) return new Clause(start, s.Length, "end of statement");
        var first = ends.MinBy(e => e.At);
        return new Clause(start, first.At, first.By);
    }

    public List<string> FieldsIn(string s, IEnumerable<(int Start, int End)> spans)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (a, b) in spans)
        {
            var part = s[a..b];
            foreach (var (field, term) in _terms)
                if (term.IsMatch(part)) found.Add(field);
        }
        return [.. found];
    }

// A field named only after a qualifier_watch word ("a possible infected ulcer") does not gate (author,
// 4 Oct 2026); named once without one, it still gates.
    private (List<string> Gating, List<string> Qualified) ClauseFields(string s, IEnumerable<Clause> clauses)
    {
        var gating = new SortedSet<string>(StringComparer.Ordinal);
        var qualified = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in clauses)
        {
            var part = s[c.Start..c.End];
            foreach (var (field, term) in _terms)
                foreach (Match m in term.Matches(part))
                {
                    if (_qualifier.IsMatch(part[..m.Index])) qualified.Add(field);
                    else gating.Add(field);
                }
        }
        qualified.ExceptWith(gating);
        return ([.. gating], [.. qualified]);
    }

    private static List<(int, int)> Outside(int length, IEnumerable<Clause> clauses)
    {
        var spans = new List<(int, int)>();
        var at = 0;
        foreach (var c in clauses.OrderBy(c => c.Start))
        {
            if (c.Start > at) spans.Add((at, c.Start));
            at = Math.Max(at, c.End);
        }
        if (at < length) spans.Add((at, length));
        return spans;
    }

    public ChunkAnnotation Annotate(RecommendationChunk chunk)
    {
        var s = BareStatement(chunk.Text);
        var a = ClauseA(s);
        var cs = ClausesC(s);
        var flags = new List<string>();

        List<Clause> clauseA = a is null ? [] : [a];
        var (reqA, qualifiedA) = ClauseFields(s, clauseA);
        var (reqC, qualifiedC) = ClauseFields(s, cs);
        var menA = Merged(FieldsIn(s, Outside(s.Length, clauseA)), qualifiedA);
        var menC = Merged(FieldsIn(s, Outside(s.Length, cs)), qualifiedC);

// Polarity is read where the main clause opens: after the variant-A clause, or from the start.
        var main = a is null ? s : s[a.End..].TrimStart(',', ':', ' ');
        var polarity = _prohibit.Any(p => p.IsMatch(main)) || _prohibitAnywhere.IsMatch(s) ? "PROHIBIT" : "RECOMMEND";

        if (a?.EndedBy == "end of statement") flags.Add("A clause runs to the end of the statement");
// Normal for a closing clause, but an over-long clause would hide here, so the review shows it.
        if (cs.Any(c => c.EndedBy == "end of statement")) flags.Add("C clause runs to the end of the statement");
        if (polarity == "RECOMMEND" && _negation.IsMatch(s)) flags.Add("RECOMMEND but contains a negation");
        if (!reqA.SequenceEqual(reqC)) flags.Add("A and C differ");
        if (a is null && cs.Count == 0) flags.Add("no population clause");
        if (qualifiedA.Count > 0 || qualifiedC.Count > 0) flags.Add("field after a qualifier does not gate (suspected/possible/diagnosing)");

        List<string> reqAuthor = _authorPrerequisites.TryGetValue(chunk.ChunkId, out var listed) ? [.. listed.Order(StringComparer.Ordinal)] : [];
        if (reqAuthor.Count > 0) flags.Add("prerequisites set by the author");

        return new ChunkAnnotation(chunk.ChunkId, a, cs, reqA, reqC, reqAuthor, menA, menC, polarity, flags);
    }

    public static List<LocatedThreshold> LocateThresholds(AnnotationFileDto file, IReadOnlyList<RecommendationChunk> chunks)
    {
        var result = new List<LocatedThreshold>();
        foreach (var t in file.Thresholds)
        {
            var chunk = chunks.SingleOrDefault(c => c.ChunkId == t.ChunkId)
                ?? throw new InvalidDataException($"threshold {t.ThresholdId}: no chunk {t.ChunkId}");
            var s = BareStatement(chunk.Text);
            var at = new List<int>();
            for (var i = s.IndexOf(t.Token, StringComparison.Ordinal); i >= 0; i = s.IndexOf(t.Token, i + 1, StringComparison.Ordinal))
                at.Add(i);
            if (at.Count != 1)
                throw new InvalidDataException($"threshold {t.ThresholdId}: token found {at.Count} times in {t.ChunkId}, expected once");
            var number = Regex.Match(t.Token, @"\d+(?:\.\d+)?").Value;
            if (decimal.Parse(number, System.Globalization.CultureInfo.InvariantCulture) != t.Value)
                throw new InvalidDataException($"threshold {t.ThresholdId}: token {t.Token} does not carry value {t.Value}");
            result.Add(new LocatedThreshold(t.ThresholdId, t.Value, t.Unit, SentenceAround(s, at[0]), chunk.PdfIndex, chunk.ChunkId));
        }
        return result;
    }

// From the previous ". " to the next; a decimal point is no sentence end, as ". " needs a space.
    private static string SentenceAround(string s, int i)
    {
        var start = s.LastIndexOf(". ", i, StringComparison.Ordinal);
        var end = s.IndexOf(". ", i, StringComparison.Ordinal);
        return s[(start < 0 ? 0 : start + 2)..(end < 0 ? s.Length : end + 1)].Trim();
    }
}

public static class AnnotationFile
{
    public static AnnotationFileDto? Read(string path) =>
        File.Exists(path) ? AliasFile.Deserializer().Deserialize<AnnotationFileDto>(File.ReadAllText(path)) : null;
}
