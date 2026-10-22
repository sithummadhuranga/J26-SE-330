// The result of starting a session; SessionId is set when it was started or already existed.

namespace WoundRag.Infrastructure.Chat;

public sealed record ChatStart(ChatStartKind Kind, Guid? SessionId);
