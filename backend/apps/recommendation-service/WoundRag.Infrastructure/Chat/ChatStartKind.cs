// What starting a chat session found.

namespace WoundRag.Infrastructure.Chat;

public enum ChatStartKind
{
    Started,
    Existing,
    // No stored answer (with a chat context) for this caseId and revision.
    NoAnswer,
    // The answer came from another corpus version than the one the service now answers from.
    CorpusDiffers,
}
