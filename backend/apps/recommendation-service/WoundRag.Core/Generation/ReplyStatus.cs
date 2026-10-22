// How one model call ended, as the chain needs to know it.

namespace WoundRag.Core.Generation;

public enum ReplyStatus
{
    Ok,
    // Timeout, HTTP 429, 5xx or no connection: the next model may answer.
    Unavailable,
    // Anything else (400, 401, 403, an unexpected body): a fault the fallback must not hide.
    Rejected,
}
