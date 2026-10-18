// Reads response_sections.yaml, embedded in this assembly so it ships with the service image (decision 75). Strict:
// a repeated chunk ID, an unknown section or a missing corpus version throws, so no entry is silently dropped.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using WoundRag.Core.Composition;
using WoundRag.Core.Generation;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WoundRag.Infrastructure.Composition;

public static class SectionPlacementFile
{
    public static SectionPlacement Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("response_sections.yaml")
            ?? throw new InvalidOperationException("embedded response_sections.yaml not found");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var bytes = copy.ToArray();
        return Parse(Encoding.UTF8.GetString(bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public static SectionPlacement Parse(string yaml) => Parse(yaml, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))));

    private static SectionPlacement Parse(string yaml, string sha256)
    {
        // YamlDotNet keeps the last of two equal keys without a word; the check makes a repeat throw.
        var file = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).WithDuplicateKeyChecking()
            .Build().Deserialize<FileContent>(yaml);
        if (string.IsNullOrWhiteSpace(file.CorpusVersion)) throw new InvalidDataException("response_sections.yaml has no corpus_version");
        foreach (var (chunkId, section) in file.Sections)
            if (!ResponseSections.All.Contains(section))
                throw new InvalidDataException($"response_sections.yaml: {chunkId} has unknown section '{section}'");
        return new SectionPlacement(file.Status, file.CorpusVersion, file.Sections, sha256);
    }

    private sealed class FileContent
    {
        public string Status { get; set; } = "proposed";
        public string? ConfirmedBy { get; set; }
        public string? ConfirmedOn { get; set; }
        public string CorpusVersion { get; set; } = "";
        public Dictionary<string, string> Sections { get; set; } = [];
    }
}
