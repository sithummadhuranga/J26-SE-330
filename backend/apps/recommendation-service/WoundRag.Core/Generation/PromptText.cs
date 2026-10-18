// One prompt: the fixed instructions as the system message, the findings and passages as the user message.

using System.Security.Cryptography;
using System.Text;

namespace WoundRag.Core.Generation;

public sealed record PromptText(string System, string User)
{
    // The NUL keeps the two parts apart, so no other split of the same characters hashes alike.
    public string Hash => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(System + "\0" + User)));
}
