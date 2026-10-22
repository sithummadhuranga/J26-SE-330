// A save's result; StoredResponse is the earlier answer when the same body was already answered.

namespace WoundRag.Infrastructure.Requests;

public sealed record SaveOutcome(SaveStatus Status, byte[]? StoredResponse);
