using System.Text.Json;
using WinnersPortal.Domain;
using WinnersPortal.Services.Ai;

namespace WinnersPortal.AiEvals;

/// <summary>
/// What the gate (<see cref="AiOutputs.Validate"/>) repaired on its way to
/// the answer a person reads: text cut to its box, items dropped, a key
/// cleaned, points rescaled to a hundred, JSON taken out of a fence. The
/// checks score what the person gets, so a repaired slip can pass; this
/// says it happened, so the report still shows how often the model makes
/// it. Read by setting the model's own JSON beside the gate's, path by
/// path — no rule here restates a limit the validator holds.
/// </summary>
public static class EvalRepairs
{
    /// <summary>The marker <see cref="AiRules.Clip"/> leaves where it cut.</summary>
    private const string ClipMarker = "\n[…clipped]";

    public static IReadOnlyList<string> Find(AiFeature feature, string modelText, string canonical)
    {
        var repairs = new List<string>();
        var json = AiRules.ExtractJson(modelText);
        if (json is null) return repairs;
        if (modelText.Trim() != json)
            repairs.Add(modelText.TrimStart().StartsWith("```", StringComparison.Ordinal)
                ? "the gate took the JSON out of a code fence"
                : "the gate took the JSON out of the text around it");

        JsonDocument raw;
        try
        {
            raw = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return repairs;
        }
        using (raw)
        using (var gated = JsonDocument.Parse(canonical))
        {
            // The rubric's points are rescaled as a whole: one line for the
            // total rather than one per criterion the rescale moved.
            var skipPoints = false;
            if (feature == AiFeature.CriteriaSuggestion && Points(raw.RootElement) is { } given
                && Points(gated.RootElement) is { } kept && given.Count == kept.Count && !given.SequenceEqual(kept))
            {
                repairs.Add($"the gate rescaled the points from a total of {given.Sum()} to {kept.Sum()}");
                skipPoints = true;
            }
            Compare("", raw.RootElement, gated.RootElement, repairs, skipPoints);
        }
        return repairs;
    }

    private static List<int>? Points(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("criteria", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray()
                .Select(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Number
                    ? (int)Math.Round(p.GetDouble())
                    : 0)
                .ToList()
            : null;

    private static void Compare(string path, JsonElement raw, JsonElement gated, List<string> repairs, bool skipPoints)
    {
        switch (gated.ValueKind)
        {
            case JsonValueKind.Object:
                if (raw.ValueKind != JsonValueKind.Object)
                {
                    repairs.Add($"the gate rebuilt {Name(path)} from {Kind(raw)}");
                    return;
                }
                foreach (var p in gated.EnumerateObject())
                {
                    var at = Join(path, p.Name);
                    if (raw.TryGetProperty(p.Name, out var r)) Compare(at, r, p.Value, repairs, skipPoints);
                    else if (!Blank(p.Value)) repairs.Add($"the gate filled in {at}, which the answer left out");
                }
                foreach (var p in raw.EnumerateObject())
                    if (!gated.TryGetProperty(p.Name, out _) && !Blank(p.Value))
                        repairs.Add($"the gate dropped {Join(path, p.Name)} ({Short(p.Value)})");
                break;

            case JsonValueKind.Array:
                if (raw.ValueKind != JsonValueKind.Array)
                {
                    repairs.Add($"the gate rebuilt {Name(path)} from {Kind(raw)}");
                    return;
                }
                CompareArray(path, raw, gated, repairs, skipPoints);
                break;

            case JsonValueKind.String:
            {
                var kept = gated.GetString() ?? "";
                if (raw.ValueKind != JsonValueKind.String)
                {
                    repairs.Add($"the gate changed {Name(path)} from {Short(raw)} to {Quote(kept)}");
                    return;
                }
                var given = (raw.GetString() ?? "").Trim();
                if (given == kept || SameGuid(given, kept)) return;
                var body = kept.EndsWith(ClipMarker, StringComparison.Ordinal) ? kept[..^ClipMarker.Length] : kept;
                if (given.Length > body.Length && given.StartsWith(body.TrimEnd(), StringComparison.Ordinal))
                    repairs.Add($"the gate cut {Name(path)} from {given.Length} to {body.TrimEnd().Length} characters");
                else
                    repairs.Add($"the gate changed {Name(path)} from {Quote(given)} to {Quote(kept)}");
                break;
            }

            case JsonValueKind.Number:
                if (skipPoints && path.EndsWith(".points", StringComparison.Ordinal)) return;
                if (raw.ValueKind != JsonValueKind.Number || raw.GetDecimal() != gated.GetDecimal())
                    repairs.Add($"the gate changed {Name(path)} from {Short(raw)} to {gated.GetRawText()}");
                break;

            case JsonValueKind.True or JsonValueKind.False:
                if (raw.ValueKind != gated.ValueKind)
                    repairs.Add($"the gate read {Name(path)} {Short(raw)} as {gated.GetRawText()}");
                break;

            case JsonValueKind.Null:
                if (!Blank(raw)) repairs.Add($"the gate dropped {Name(path)} ({Short(raw)})");
                break;
        }
    }

    /// <summary>
    /// Lines the model answered by id are matched by id, so a dropped line
    /// is named and the rest still compared; a list of words names the
    /// words dropped; any other list compares item by item while nothing
    /// was dropped, and says how many were when something was.
    /// </summary>
    private static void CompareArray(string path, JsonElement raw, JsonElement gated, List<string> repairs, bool skipPoints)
    {
        var rawItems = raw.EnumerateArray().ToList();
        var keptItems = gated.EnumerateArray().ToList();

        if (keptItems.Concat(rawItems).All(i => i.ValueKind == JsonValueKind.String))
        {
            var kept = keptItems.Select(i => i.GetString() ?? "").ToList();
            if (rawItems.Count == keptItems.Count)
            {
                for (var i = 0; i < kept.Count; i++) Compare($"{path}[{i + 1}]", rawItems[i], keptItems[i], repairs, skipPoints);
                return;
            }
            var dropped = rawItems.Select(i => i.GetString() ?? "")
                .Where(s => !string.IsNullOrWhiteSpace(s) && !kept.Contains(s.Trim(), StringComparer.Ordinal))
                .ToList();
            repairs.Add(dropped.Count > 0
                ? $"the gate dropped {string.Join(", ", dropped.Select(Quote))} from {Name(path)}"
                : $"the gate dropped {rawItems.Count - keptItems.Count} of {rawItems.Count} items from {Name(path)}");
            return;
        }

        var key = IdKey(keptItems);
        if (key is not null)
        {
            var byId = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rawItems)
                if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty(key, out var id) && id.ValueKind == JsonValueKind.String)
                    byId.TryAdd(id.GetString()!.Trim(), r);
            foreach (var k in keptItems)
            {
                var id = k.GetProperty(key).GetString()!;
                if (byId.Remove(id, out var r)) Compare($"{path}[{id}]", r, k, repairs, skipPoints);
            }
            var lost = rawItems.Count - keptItems.Count;
            if (lost > 0) repairs.Add($"the gate dropped {lost} of {rawItems.Count} items from {Name(path)}");
            return;
        }

        if (rawItems.Count == keptItems.Count)
        {
            for (var i = 0; i < keptItems.Count; i++) Compare($"{path}[{i + 1}]", rawItems[i], keptItems[i], repairs, skipPoints);
            return;
        }
        repairs.Add($"the gate dropped {rawItems.Count - keptItems.Count} of {rawItems.Count} items from {Name(path)}");
    }

    private static string? IdKey(List<JsonElement> items) =>
        items.Count > 0 && items.All(i => i.ValueKind == JsonValueKind.Object)
            ? new[] { "id", "entryId" }.FirstOrDefault(k => items.All(i => i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String))
            : null;

    private static bool SameGuid(string a, string b) => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y;

    private static bool Blank(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(e.GetString()),
        JsonValueKind.Array => e.GetArrayLength() == 0,
        _ => false,
    };

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static string Name(string path) => path.Length == 0 ? "the answer" : path;

    private static string Kind(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Array => "a list",
        JsonValueKind.Object => "an object",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        _ => e.ValueKind.ToString().ToLowerInvariant(),
    };

    private static string Short(JsonElement e) => e.ValueKind == JsonValueKind.String ? Quote(e.GetString() ?? "") : Clip(e.GetRawText(), 40);

    private static string Quote(string s) => "“" + Clip(s, 40) + "”";

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
