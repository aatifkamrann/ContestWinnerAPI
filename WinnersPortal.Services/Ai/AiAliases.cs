using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// The labels entrants travel under where a prompt needs to tell them apart
/// and nothing more — E1, E2 and so on, in the order the input lists them.
/// The progress narrative and the standing notes describe what each
/// entrant did, and for that the model needs to know which row is which;
/// it has no use for the person's name, which is personal data sent to a
/// third party for no reason the feature can give. So the input carries
/// the label, the names stay here, and the answer has them put back before
/// anyone reads it. A side effect is a cache key that no longer moves when
/// somebody renames their account.
///
/// Restoring is a whole-word swap of the labels this instance handed out:
/// "E1 claimed two milestones" becomes the name, "E2E tests" is left alone,
/// and a label the model made up (E9 on a board of three) stays as written,
/// because there is nothing truthful to put in its place.
/// </summary>
public sealed class AiAliases
{
    private static readonly Regex Label = new(
        @"(?<![\p{L}\p{N}])E[1-9]\d{0,2}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant);

    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    /// <summary>The label of the entrant at a one-based position.</summary>
    public static string LabelOf(int position) => $"E{position}";

    public int Count => _names.Count;

    /// <summary>Hands out the next label for a name, and says which it was.</summary>
    public string Add(string name)
    {
        var label = LabelOf(_names.Count + 1);
        _names[label] = name;
        return label;
    }

    /// <summary>The name a label stands for, or null for a label never handed out.</summary>
    public string? NameOf(string label) => _names.GetValueOrDefault(label);

    /// <summary>The names back into plain text, whole labels only.</summary>
    public string Restore(string text) =>
        Label.Replace(text, m => _names.TryGetValue(m.Value, out var name) ? name : m.Value);

    /// <summary>
    /// The names back into canonical JSON, each written the way the
    /// serializer would have written it — escaped, so the text stays JSON
    /// whatever the name holds. The labels are ASCII letters and digits,
    /// which no serializer escapes, so a whole-word match on the JSON text
    /// is exactly the match on the strings inside it.
    /// </summary>
    public string RestoreJson(string json) =>
        Label.Replace(json, m => _names.TryGetValue(m.Value, out var name) ? JsonEncodedText.Encode(name).ToString() : m.Value);
}
