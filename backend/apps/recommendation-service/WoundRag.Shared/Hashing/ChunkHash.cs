// chunk_hash = SHA-256 over NFC(corpus_version || chunk_id || text), the same expression A9 runs in SQL.
// No separator is needed: chunk IDs start with an upper-case code and versions are lower case.

using System.Security.Cryptography;
using System.Text;

namespace WoundRag.Shared.Hashing;

public static class ChunkHash
{
    public static string Compute(string corpusVersion, string chunkId, string text) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes((corpusVersion + chunkId + text).Normalize(NormalizationForm.FormC))));
}
