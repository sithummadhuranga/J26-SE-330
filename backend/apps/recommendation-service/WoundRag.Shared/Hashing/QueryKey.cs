// query_key = SHA-256(query_text || '|' || retrieval_filter), the one definition the embed step and the service share.
// No NFC, unlike ChunkHash: the database CHECK hashes the stored text as is, and the text is built from fixed templates.

using System.Security.Cryptography;
using System.Text;

namespace WoundRag.Shared.Hashing;

public static class QueryKey
{
    public static string Compute(string queryText, string retrievalFilter) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(queryText + "|" + retrievalFilter)));
}
