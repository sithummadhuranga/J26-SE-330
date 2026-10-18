// The four headings the protocol is written under, in the order the prompt lists them and the answer must follow.

namespace WoundRag.Core.Generation;

public static class ResponseSections
{
    public const string Management = "management";
    public const string Contraindications = "contraindications";
    public const string Trajectory = "trajectory";
    public const string AssessmentInstructions = "assessmentInstructions";

    public static readonly IReadOnlyList<string> All = [Management, Contraindications, Trajectory, AssessmentInstructions];

    // What the clinician reads on Member 4's screen (decision 75); the prompt keeps the names above.
    public static string Heading(string section) => section switch
    {
        Management => "Management",
        Contraindications => "Contraindications",
        Trajectory => "Healing trajectory",
        AssessmentInstructions => "Assessment instructions",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "not a response section"),
    };
}
