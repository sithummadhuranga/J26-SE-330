// What the model's answer turned out to be.

namespace WoundRag.Core.Chat.Checks;

public enum ChatAnswerKind
{
    Answered,
    NotCovered,
    OutOfScope,
    Rejected,
}
