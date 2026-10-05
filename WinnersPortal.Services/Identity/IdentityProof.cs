using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinnersPortal.Services.Identity;

/// <summary>
/// What the portal keeps as proof of a verification, decided without a
/// database or a network: which values in a provider decision are images
/// to copy, what each is called on screen, where it is stored, the few
/// facts an administrator reads first, and how the copy retries. Pure, so
/// all of it is pinned by tests; <see cref="IdentityProofWorker"/> does the
/// copying.
/// </summary>
public static class IdentityProof
{
    /// <summary>The largest image copied; anything bigger is left as the provider's link.</summary>
    public const long MaxImageBytes = 20L * 1024 * 1024;

    /// <summary>Tries before the worker gives up on a decision and says why on the panel.</summary>
    public const int MaxAttempts = 6;

    /// <summary>
    /// Whether a verdict queues its proof: one the provider has a decision
    /// behind — approved, declined, or with its reviewer — whose status
    /// changed, or whose decision was never copied nor tried. A decision
    /// already queued, or given up on, is left to the worker and to the
    /// administrator's Fetch again: a member reloading the return page must
    /// not restart the tries. A session still in progress has nothing to prove.
    /// </summary>
    public static bool Due(Domain.IdentityStatus status, bool changed, bool haveDecision, bool triedBefore) =>
        status is Domain.IdentityStatus.Approved or Domain.IdentityStatus.Declined or Domain.IdentityStatus.InReview
        && (changed || (!haveDecision && !triedBefore));

    /// <summary>2, 4, 8 … minutes after each failed try, never more than an hour.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Max(attempts, 1)), 60));

    /// <param name="AccessToken">
    /// Shufti Pro's decision-wide access token, posted to fetch the image;
    /// null for Didit's links, which are signed and fetched as they are.
    /// </param>
    public sealed record Image(string Name, string Url, string? AccessToken = null);

    /// <summary>
    /// The images in a decision from <paramref name="provider"/>. Didit's are
    /// found by name wherever they sit (<see cref="Images(string?)"/>).
    /// Shufti Pro keeps its under <c>proofs</c> — the document's front, its
    /// additional side, the selfie — each named "proof", with one access
    /// token for them all; heatmaps, videos and the one-time report are
    /// left as links.
    /// </summary>
    public static IReadOnlyList<Image> Images(string provider, string? decisionJson)
    {
        if (provider != IdentityProviders.ShuftiPro) return Images(decisionJson);
        var found = new List<Image>();
        if (string.IsNullOrWhiteSpace(decisionJson)) return found;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(decisionJson);
        }
        catch (JsonException)
        {
            return found;
        }
        if (root?["proofs"] is not JsonObject proofs) return found;
        var token = proofs["access_token"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : null;
        foreach (var (service, value) in proofs)
        {
            if (value is not JsonObject parts) continue;
            foreach (var (name, link) in parts)
            {
                if (!name.Contains("proof", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("heatmap", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("video", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (link is JsonValue v && v.GetValueKind() == JsonValueKind.String
                    && Uri.TryCreate(v.GetValue<string>(), UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                    found.Add(new Image($"proofs.{service}.{name}", uri.ToString(), token));
            }
        }
        return found;
    }

    /// <summary>
    /// Every image link in a decision, in document order: a string that is
    /// an http(s) address under a name that says image — "front_image",
    /// "portrait_image", "reference_image", "source_image", or a list of
    /// them — wherever it sits. Videos are left as links: they are large,
    /// and the stills are the proof. Each named by its path in the decision.
    /// </summary>
    public static IReadOnlyList<Image> Images(string? decisionJson)
    {
        var found = new List<Image>();
        if (string.IsNullOrWhiteSpace(decisionJson)) return found;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(decisionJson);
        }
        catch (JsonException)
        {
            return found;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(root, "", imageName: false, found, seen);
        return found;
    }

    private static void Walk(JsonNode? node, string path, bool imageName, List<Image> found, HashSet<string> seen)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                    Walk(value, path.Length == 0 ? name : $"{path}.{name}", SaysImage(name), found, seen);
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    Walk(array[i], $"{path}[{i}]", imageName, found, seen);
                break;
            case JsonValue value when imageName
                && value.GetValueKind() == JsonValueKind.String
                && value.GetValue<string>() is { } url
                && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && seen.Add(url):
                found.Add(new Image(path, url));
                break;
        }
    }

    private static bool SaysImage(string name) =>
        name.Contains("image", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("video", StringComparison.OrdinalIgnoreCase);

    /// <summary>"id_verification.front_image" → "ID verification · Front image"; list positions dropped.</summary>
    public static string Label(string name)
    {
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('[')[0])
            .Where(p => p.Length > 0)
            .TakeLast(2)
            .Select(Words);
        return string.Join(" · ", parts);
    }

    private static string Words(string snake)
    {
        var words = snake.Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Equals("id", StringComparison.OrdinalIgnoreCase) ? "ID" : w.ToLowerInvariant())
            .ToList();
        if (words.Count == 0) return snake;
        if (words[0] != "ID") words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
        return string.Join(' ', words);
    }

    /// <summary>The file extension a stored copy gets from its type.</summary>
    public static string Extension(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "jpg",
        "image/png" => "png",
        "image/webp" => "webp",
        "image/gif" => "gif",
        "image/heic" => "heic",
        "application/pdf" => "pdf",
        _ => "bin",
    };

    /// <summary>Whether a downloaded body is something to keep as an image: an image, or a PDF of one.</summary>
    public static bool Keepable(string? contentType) =>
        contentType is not null
        && (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a copy lives in storage: one folder per member and verification, numbered in decision order.</summary>
    public static string StorageKey(Guid userId, Guid verificationId, int index, string name, string? contentType)
    {
        var slug = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            slug.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        var clean = string.Join('-', slug.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 80) clean = clean[..80];
        return $"identity/{userId:N}/{verificationId:N}/{index:00}-{clean}.{Extension(contentType)}";
    }

    public sealed record Fact(string Label, string Value);

    /// <summary>The first facts of a decision from <paramref name="provider"/>: Didit's as below, Shufti Pro's from its verification data and result.</summary>
    public static IReadOnlyList<Fact> Summary(string provider, string? decisionJson) =>
        provider == IdentityProviders.ShuftiPro ? ShuftiSummary(decisionJson) : Summary(decisionJson);

    /// <summary>
    /// Shufti Pro's decision: what its OCR read off the document under
    /// verification_data.document, and the verdict per service under
    /// verification_result (1 accepted, 0 declined).
    /// </summary>
    private static IReadOnlyList<Fact> ShuftiSummary(string? decisionJson)
    {
        var facts = new List<Fact>();
        if (string.IsNullOrWhiteSpace(decisionJson)) return facts;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(decisionJson);
        }
        catch (JsonException)
        {
            return facts;
        }
        if (root is not JsonObject top) return facts;
        if (top["verification_data"]?["document"] is JsonObject document)
        {
            var name = document["name"] as JsonObject;
            Add(facts, "Name", name is null
                ? Text(document, "name")
                : Text(name, "full_name")
                    ?? string.Join(' ', new[] { Text(name, "first_name"), Text(name, "middle_name"), Text(name, "last_name") }
                        .Where(s => !string.IsNullOrWhiteSpace(s))));
            var type = document["selected_type"] is JsonArray types ? types.FirstOrDefault()?.ToString() : Text(document, "selected_type");
            Add(facts, "Document", type?.Replace('_', ' '));
            Add(facts, "Document number", Text(document, "document_number"));
            Add(facts, "Date of birth", Text(document, "dob"));
            Add(facts, "Issued by", Text(document, "country") ?? Text(top, "country"));
            Add(facts, "Issued", Text(document, "issue_date"));
            Add(facts, "Expires", Text(document, "expiry_date"));
        }
        if (top["verification_result"] is JsonObject result)
        {
            Add(facts, "Document check", Verdict(result["document"]));
            Add(facts, "Face match", Verdict(result["face"]));
        }
        Add(facts, "Declined because", Text(top, "declined_reason"));
        return facts;
    }

    /// <summary>A Shufti Pro service's verdict, 1 or 0 — or an object of them, where any 0 declines it.</summary>
    private static string? Verdict(JsonNode? node) => node switch
    {
        JsonValue v when v.ToString() == "1" => "Accepted",
        JsonValue v when v.ToString() == "0" => "Declined",
        JsonObject parts => parts.Select(p => p.Value?.ToString()).Where(s => s is "0" or "1").ToList() is { Count: > 0 } said
            ? said.Contains("0") ? "Declined" : "Accepted"
            : null,
        _ => null,
    };

    /// <summary>
    /// The handful of things an administrator reads first — whose document,
    /// which document, what it says and how the checks scored — out of the
    /// document check (id_verification, or the first of id_verifications),
    /// the liveness check and the face match, wherever the decision nests
    /// them. Everything else is in the full decision under it.
    /// </summary>
    public static IReadOnlyList<Fact> Summary(string? decisionJson)
    {
        var facts = new List<Fact>();
        if (string.IsNullOrWhiteSpace(decisionJson)) return facts;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(decisionJson);
        }
        catch (JsonException)
        {
            return facts;
        }
        if (root is not JsonObject top) return facts;
        var scope = top["decision"] as JsonObject ?? top;

        var document = Check(scope, "id_verification", "id_verifications");
        if (document is not null)
        {
            var name = Text(document, "full_name")
                ?? string.Join(' ', new[] { Text(document, "first_name"), Text(document, "last_name") }.Where(s => s is not null));
            Add(facts, "Name", name);
            Add(facts, "Document", Text(document, "document_type"));
            Add(facts, "Document number", Text(document, "document_number"));
            Add(facts, "Personal number", Text(document, "personal_number"));
            Add(facts, "Date of birth", Text(document, "date_of_birth"));
            Add(facts, "Nationality", Text(document, "nationality"));
            Add(facts, "Issued by", Text(document, "issuing_state_name") ?? Text(document, "issuing_state"));
            Add(facts, "Issued", Text(document, "date_of_issue"));
            Add(facts, "Expires", Text(document, "expiration_date"));
            Add(facts, "Address", Text(document, "formatted_address") ?? Text(document, "address"));
            Add(facts, "Document check", Text(document, "status"));
        }
        if (Check(scope, "liveness", "liveness_checks") is { } liveness)
            Add(facts, "Liveness", Scored(liveness));
        if (Check(scope, "face_match", "face_matches") is { } face)
            Add(facts, "Face match", Scored(face));
        return facts;
    }

    private static JsonObject? Check(JsonObject scope, string one, string many) =>
        scope[one] as JsonObject ?? (scope[many] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();

    private static string? Scored(JsonObject check)
    {
        var status = Text(check, "status");
        var score = check["score"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.ToString() : null;
        return status is null ? score : score is null ? status : $"{status} · score {score}";
    }

    private static string? Text(JsonObject obj, string name) => obj[name] switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToString(),
        _ => null,
    };

    private static void Add(List<Fact> facts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) facts.Add(new Fact(label, value.Trim()));
    }

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The decision re-indented for reading; as it came when it is not JSON.</summary>
    public static string Pretty(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(Indented) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
