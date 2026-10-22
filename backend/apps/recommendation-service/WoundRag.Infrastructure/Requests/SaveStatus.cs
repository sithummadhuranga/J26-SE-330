// How a save ended: stored, refused because the key holds another request, or beaten to it by the same body.

namespace WoundRag.Infrastructure.Requests;

public enum SaveStatus
{
    Saved,
    Conflict,
    AlreadyAnswered,
}
