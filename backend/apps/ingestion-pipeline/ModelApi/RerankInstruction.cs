// The rerank instruction (decision 34), the one the smoke test proved the API counts. Its SHA-256 is stored with
// every score and in the manifest; changing it means scoring again.

using System.Security.Cryptography;
using System.Text;

namespace IngestionPipeline.ModelApi;

public static class RerankInstruction
{
    public const string Text = "Given a diabetic foot ulcer assessment query, find IWGDF 2023 guideline passages that answer it.";

    public static readonly string Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
}
