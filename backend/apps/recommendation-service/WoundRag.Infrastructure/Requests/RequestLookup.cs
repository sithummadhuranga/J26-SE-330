// What is stored under a request key: its stable hash, and the answer sent for this exact body if there is one.

namespace WoundRag.Infrastructure.Requests;

public sealed record RequestLookup(string StableSha256, byte[]? ResponseForBody);
