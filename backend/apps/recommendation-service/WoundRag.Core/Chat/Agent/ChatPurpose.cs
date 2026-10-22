// Why a model was called during a turn (audited).

namespace WoundRag.Core.Chat.Agent;

public static class ChatPurpose
{
    public const string Answer = "answer";
    public const string Verify = "verify";
    public const string Translate = "translate";
    public const string VerifyTranslation = "verify_translation";
}
