using WinnersPortal.Domain;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// Which model each feature asks, as the operator typed it on Settings →
/// AI automation: one feature per line, named as the AI usage panel names
/// it, then the model. A feature not on the list asks the active setup's
/// model, and a blank list means every feature does — so a line here is
/// the way to give the quick inline tools (a category, a rubric, a
/// summary) a quick model while the long reads keep the stronger one.
/// The list is read with the active setup and applies to it alone: a
/// standby setup answers with its own model, because a model named for
/// one provider is not a model the other offers.
/// </summary>
public static class AiFeatureModels
{
    public const string Key = "ai.featureModels";

    public const int MaxLines = 50;
    public const int MaxModelLength = 100;

    public const string Format =
        "one feature per line: the feature's name as the AI usage panel shows it, then the model — "
        + "\"categorySuggestion gemini-3.6-flash-lite\"";

    /// <summary>The features a line may name: every one that asks a model, under the switch's own name.</summary>
    public static readonly IReadOnlyList<string> FeatureNames =
        [.. Enum.GetValues<AiFeature>().Where(AiOptions.RequiresProvider).Select(AiPrompts.FeatureName)];

    /// <summary>Why the list cannot be stored, or null when it can — every line read, none skipped.</summary>
    public static string? Problem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var lines = Lines(value).ToList();
        if (lines.Count > MaxLines) return $"Model per feature can list at most {MaxLines} lines.";
        foreach (var (n, line) in lines)
        {
            var parts = Parts(line);
            if (parts.Length != 2)
                return $"Line {n} of model per feature could not be read ({Clip(line)}). Give {Format}.";
            if (Feature(parts[0]) is null)
                return $"Line {n} of model per feature names a feature the portal does not have ({Clip(parts[0])}). "
                    + $"The features are {string.Join(", ", FeatureNames)}.";
            if (parts[1].Length > MaxModelLength)
                return $"Line {n} of model per feature names a model longer than {MaxModelLength} characters.";
        }
        return null;
    }

    /// <summary>The list as typed, the last line for a feature winning; a value the screen would refuse reads as the lines it could.</summary>
    public static AiFeatureModelList Parse(string? value)
    {
        var models = new Dictionary<AiFeature, string>();
        foreach (var (_, line) in Lines(value))
        {
            var parts = Parts(line);
            if (parts.Length == 2 && Feature(parts[0]) is { } feature && parts[1].Length <= MaxModelLength)
                models[feature] = parts[1];
        }
        return new AiFeatureModelList(models);
    }

    /// <summary>The feature a line names, by the switch's name in any case; null for a name the portal does not have.</summary>
    public static AiFeature? Feature(string name) =>
        Enum.GetValues<AiFeature>()
            .Where(AiOptions.RequiresProvider)
            .Cast<AiFeature?>()
            .FirstOrDefault(f => string.Equals(AiPrompts.FeatureName(f!.Value), name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(int Number, string Line)> Lines(string? value)
    {
        if (value is null) yield break;
        var n = 0;
        foreach (var raw in value.Split('\n'))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            yield return (n, line);
        }
    }

    /// <summary>"feature model", the two parts split on spaces, a comma, an equals sign or a colon.</summary>
    private static string[] Parts(string line) =>
        line.Split([' ', '\t', ',', '=', ':', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Clip(string text) => text.Length <= 40 ? text : text[..40] + "…";
}

/// <summary>The models named per feature; empty while nothing is saved, and then every feature asks the setup's model.</summary>
public sealed class AiFeatureModelList(IReadOnlyDictionary<AiFeature, string> models)
{
    public static readonly AiFeatureModelList Empty = new(new Dictionary<AiFeature, string>());

    public bool Any => models.Count > 0;

    /// <summary>The model named for a feature, or null when the setup's own applies.</summary>
    public string? For(AiFeature feature) => models.TryGetValue(feature, out var m) ? m : null;
}
