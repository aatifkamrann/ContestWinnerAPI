using WinnersPortal.Domain;
using System.Security.Cryptography;
using System.Text;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// The pure mechanics of the AI layer — hashing, model-output cleanup, the
/// daily quota arithmetic. Everything here is deterministic and tested;
/// the worker and endpoints stay thin around it.
/// </summary>
public static class AiRules
{
    /// <summary>Briefs are clipped before they travel; a model does not need chapter 12.</summary>
    public const int MaxBriefChars = 8000;

    /// <summary>Provider retries before an artifact fails for the requester to see.</summary>
    public const int MaxAttempts = 3;

    /// <summary>The cache key: same input, same hash, no provider call.</summary>
    public static string InputHash(string canonicalInput)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalInput));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Models asked for JSON still love to wrap it — in markdown fences, in a
    /// polite sentence, or both. This digs out the outermost object literal;
    /// null when there is none to find.
    /// </summary>
    public static string? ExtractJson(string modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText)) return null;
        var start = modelText.IndexOf('{');
        var end = modelText.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return modelText[start..(end + 1)];
    }

    /// <summary>
    /// The note a failed draft carries, read on the page that asked for it.
    /// A provider's refusal becomes the line a person can act on; a network
    /// fault says the provider was out of reach; the portal's own words (a
    /// profile gone, an answer off its shape) stand as written.
    /// </summary>
    public static string FailureNote(Exception e) => e switch
    {
        AiProviderException p => p.Friendly,
        HttpRequestException or TaskCanceledException or TimeoutException =>
            "The AI provider could not be reached this time. Please try again in a few minutes.",
        _ => Clip(e.Message, 380),
    };

    /// <summary>Hard cap with an honest marker, so the model knows text is missing.</summary>
    public static string Clip(string? text, int maxChars)
    {
        var t = (text ?? "").Trim();
        return t.Length <= maxChars ? t : t[..maxChars] + "\n[…clipped]";
    }
}

/// <summary>URL slug ↔ feature, for the two request endpoints.</summary>
public static class AiFeatureRoutes
{
    /// <summary>
    /// The queued, opportunity-subject route. Only the spam scan is left here:
    /// everything else a client asks for while writing reads the form on
    /// screen instead (<see cref="FromFormSlug"/>), and the scan is the one
    /// that reads entries, which only a saved opportunity has. Digests are
    /// addressed by entry; narratives are scheduled; standing notes are
    /// deliberately absent so only the owner can reach them.
    /// </summary>
    public static AiFeature? FromSlug(string slug) => slug switch
    {
        "spam" => AiFeature.SpamFilter,
        _ => null,
    };

    /// <summary>
    /// The tools that read the opportunity form as it stands, answered inside
    /// the request. All six take the same input — the form on screen
    /// (<see cref="OpportunityFormSnapshot"/>) — which is exactly why none of
    /// them needs an opportunity to exist first; each reads the part of it that
    /// sits above the section it fills (<see cref="AiFormReads"/>).
    /// </summary>
    public static AiFeature? FromFormSlug(string slug) => slug switch
    {
        "categorise" => AiFeature.CategorySuggestion,
        "coach" => AiFeature.BriefCoach,
        "requirements" => AiFeature.RequirementsSuggestion,
        "milestones" => AiFeature.MilestoneExtraction,
        "criteria" => AiFeature.CriteriaSuggestion,
        "seo" => AiFeature.SeoMetadata,
        _ => null,
    };
}

/// <summary>
/// The parts of the opportunity form, and which of them each form tool reads.
/// The form is a funnel — title, brief, then every section below drafted
/// from what sits above it — and this table is that order made explicit:
/// a tool is handed the sections above the one it fills and nothing
/// below, so its answer cannot lean on a field the client has not reached.
/// The browser keeps the same table (components/AiBriefTools.tsx) to know
/// which edits make an answer stale, so the two must agree.
/// </summary>
[Flags]
public enum FormPart
{
    None = 0,
    Title = 1,
    Brief = 2,
    /// <summary>The category and subcategory.</summary>
    Kind = 4,
    Skills = 8,
    Requirements = 16,
    /// <summary>How work is handed in, and whether entries must run with Docker Compose.</summary>
    Delivery = 32,
    Milestones = 64,
    Criteria = 128,
}

public static class AiFormReads
{
    public const FormPart Everything = FormPart.Title | FormPart.Brief | FormPart.Kind | FormPart.Skills
        | FormPart.Requirements | FormPart.Delivery | FormPart.Milestones | FormPart.Criteria;

    /// <summary>What one form tool is handed; throws for a feature that does not read the form.</summary>
    public static FormPart Of(AiFeature feature) => feature switch
    {
        // The coach reviews the whole form: a brief that contradicts the
        // terms set beside it is exactly the kind of thing it is for.
        AiFeature.BriefCoach => Everything,
        AiFeature.CategorySuggestion => FormPart.Title | FormPart.Brief,
        AiFeature.RequirementsSuggestion => FormPart.Title | FormPart.Brief | FormPart.Kind | FormPart.Skills,
        AiFeature.MilestoneExtraction => FormPart.Title | FormPart.Brief | FormPart.Kind | FormPart.Skills
            | FormPart.Requirements | FormPart.Delivery,
        AiFeature.CriteriaSuggestion => FormPart.Title | FormPart.Brief | FormPart.Kind | FormPart.Requirements
            | FormPart.Delivery | FormPart.Milestones,
        AiFeature.SeoMetadata => FormPart.Title | FormPart.Brief | FormPart.Kind,
        _ => throw new ArgumentOutOfRangeException(
            nameof(feature), feature, "That feature does not read the opportunity form."),
    };
}

/// <summary>
/// The daily ceiling as arithmetic: a UTC date key and a counter, both kept
/// in settings rows. Limit zero (or garbage) blocks every provider call —
/// the paranoid reading is the safe one for a knob that spends money.
/// </summary>
public static class AiQuotaRules
{
    public static string DayKey(DateTimeOffset nowUtc) => nowUtc.UtcDateTime.ToString("yyyy-MM-dd");

    /// <summary>One call's verdict: whether it may run, and the state to store if it does.</summary>
    public static (bool Allowed, string NewDate, int NewCount) Consume(
        string? storedDate, string? storedCount, DateTimeOffset nowUtc, int limit)
    {
        var today = DayKey(nowUtc);
        var count = string.Equals(storedDate, today, StringComparison.Ordinal)
            && int.TryParse(storedCount, out var n) && n > 0 ? n : 0;
        if (limit <= 0 || count >= limit) return (false, today, count);
        return (true, today, count + 1);
    }

    /// <summary>
    /// Give a counted call back, for one the provider never ran. Only
    /// today’s count can be given back to: a call counted yesterday is
    /// history, and a count that has already rolled over is not ours to
    /// touch. Never below zero, whatever the stored value says.
    /// </summary>
    public static (string Date, int Count) Refund(
        string? storedDate, string? storedCount, DateTimeOffset nowUtc)
    {
        var today = DayKey(nowUtc);
        if (!string.Equals(storedDate, today, StringComparison.Ordinal)
            || !int.TryParse(storedCount, out var count) || count <= 0)
            return (today, 0);
        return (today, count - 1);
    }
}
