// The fixed texts of one language; "{0}" in a template is replaced by a list.

using System.Globalization;

namespace WoundRag.Core.Chat;

public sealed record ChatTextSet(string NotCovered, string OutOfScope, string Blocked, string Unavailable,
    string TranslationUnverified, string WithheldTemplate, string RemovedTemplate,
    string WithheldTopicTemplate, string WithheldTopicOneTemplate)
{
    public string Withheld(IReadOnlyList<string> fieldNames) => WithheldTemplate.Replace("{0}", string.Join(", ", fieldNames));

    public string Removed(IReadOnlyList<string> kinds) => RemovedTemplate.Replace("{0}", string.Join(", ", kinds));

    // "{0}" is the number of withheld passages that matched, "{1}" the fields that would let them through.
    public string WithheldTopic(int count, IReadOnlyList<string> fieldNames) =>
        (count == 1 ? WithheldTopicOneTemplate : WithheldTopicTemplate)
            .Replace("{0}", count.ToString(CultureInfo.InvariantCulture)).Replace("{1}", string.Join(", ", fieldNames));
}
