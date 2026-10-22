namespace Sync.Common.Evaluation;

/// <summary>A switch just for the evaluation. It turns off duplicate removal and writes shadow rows so the duplicates can be seen.</summary>
public sealed record AblationOptions(bool Enabled)
{
    public const string ConfigKey = "Ablation:Enabled";

    public const string Warning =
        "ABLATION MODE (evaluation only, §13): duplicate protection is partly switched off and shadow rows are recorded " +
        "in the ablation schema. Unset Ablation__Enabled for normal operation.";
}
