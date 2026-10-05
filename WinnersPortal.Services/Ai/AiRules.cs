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

    /// <summary>
    /// The cache key: same input, same prompt, same hash, no provider call.
    /// The prompt's version (<see cref="AiPrompts.Version"/>) is part of it,
    /// so a reworded prompt is a changed input and its cached answers are
    /// re-drafted rather than served. The spam scan has no prompt and
    /// hashes its input alone.
    /// </summary>
    public static string InputHash(AiFeature feature, string canonicalInput) =>
        AiOptions.RequiresProvider(feature)
            ? InputHash($"prompt v{AiPrompts.Version(feature)}\n{canonicalInput}")
            : InputHash(canonicalInput);

    /// <summary>SHA-256 of the text, lower-case hex — the mechanics under the cache key.</summary>
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

    /// <summary>
    /// Whether a failed job is worth trying again later: a provider's bad
    /// minute (a 429, a 5xx, the pause after repeated failures), an
    /// attempt that timed out or a connection that never answered, a
    /// cut-off answer or an empty one may well pass next time. Nothing
    /// else is — the provider declining the input is its reading of that
    /// input, a 4xx was the request's own fault, and an answer off its
    /// shape or a subject that is gone will be the same an hour from now;
    /// those fail on the first attempt, so the remaining ones are not spent.
    /// </summary>
    public static bool Retryable(Exception e) => e switch
    {
        AiProviderException p => p.StatusCode is 429 or >= 500
            || p.Failure is AiFailure.Truncated or AiFailure.Empty or AiFailure.Timeout or AiFailure.Paused,
        HttpRequestException or TaskCanceledException or TimeoutException => true,
        _ => false,
    };

    /// <summary>
    /// Whether the provider never ran the call, so the counted unit is given
    /// back: a 429, a 5xx, or the portal's own pause. Not a timed-out
    /// attempt — the provider may have run it and billed it.
    /// </summary>
    public static bool NothingRan(Exception e) =>
        e is AiProviderException { StatusCode: 429 or >= 500 } or AiProviderException { Failure: AiFailure.Paused };

    /// <summary>
    /// Whether the provider could not be reached at all, so a standby
    /// setup may be asked the same prompt: a 429, a 5xx, the portal's own
    /// pause, a call no attempt answered in time, or a connection that
    /// never answered. Not a refusal, a 4xx or an answer off its shape —
    /// those are the active provider's reading of the request, and
    /// another provider is not asked to overrule it.
    /// </summary>
    public static bool Unreached(Exception e) =>
        e is HttpRequestException
        || e is AiProviderException { StatusCode: 429 or >= 500 }
        || e is AiProviderException { Failure: AiFailure.Paused or AiFailure.Timeout };

    /// <summary>
    /// How long a job waits before its next attempt: a minute after the
    /// first failure, five after the second. The resilience pipeline has
    /// already retried within the call; this is the wait between calls,
    /// long enough for a provider's bad minute to pass.
    /// </summary>
    public static TimeSpan Backoff(int attempts) =>
        attempts <= 1 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5);

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
    /// <summary>How the opportunity pays: paid by milestone, with the total the milestones share. Absent on a competitive one.</summary>
    Pay = 256,
}

public static class AiFormReads
{
    public const FormPart Everything = FormPart.Title | FormPart.Brief | FormPart.Kind | FormPart.Skills
        | FormPart.Requirements | FormPart.Delivery | FormPart.Milestones | FormPart.Criteria | FormPart.Pay;

    /// <summary>What one form tool is handed; throws for a feature that does not read the form.</summary>
    public static FormPart Of(AiFeature feature) => feature switch
    {
        // The coach reviews the whole form: a brief that contradicts the
        // terms set beside it is exactly the kind of thing it is for.
        AiFeature.BriefCoach => Everything,
        AiFeature.CategorySuggestion => FormPart.Title | FormPart.Brief,
        AiFeature.RequirementsSuggestion => FormPart.Title | FormPart.Brief | FormPart.Kind | FormPart.Skills,
        // The payment terms sit above the milestones on the form: paid by
        // milestone, the draft splits the client's total across them.
        AiFeature.MilestoneExtraction => FormPart.Title | FormPart.Brief | FormPart.Kind | FormPart.Skills
            | FormPart.Requirements | FormPart.Delivery | FormPart.Pay,
        AiFeature.CriteriaSuggestion => FormPart.Title | FormPart.Brief | FormPart.Kind | FormPart.Requirements
            | FormPart.Delivery | FormPart.Milestones,
        AiFeature.SeoMetadata => FormPart.Title | FormPart.Brief | FormPart.Kind,
        _ => throw new ArgumentOutOfRangeException(
            nameof(feature), feature, "That feature does not read the opportunity form."),
    };
}

/// <summary>
/// The daily ceiling and the per-member caps as words and arithmetic: the
/// UTC day a count belongs to, and what a person is told when a cap stops
/// their press. The counts themselves are rows of AiUsage, taken by
/// <see cref="AiQuota"/> in a single statement each.
/// </summary>
public static class AiQuotaRules
{
    public static string DayKey(DateTimeOffset nowUtc) => nowUtc.UtcDateTime.ToString("yyyy-MM-dd");

    /// <summary>The line a member reads when a cap stopped the call before anything was sent.</summary>
    public static string Refusal(AiQuotaVerdict verdict) => verdict switch
    {
        AiQuotaVerdict.MemberBurst => "You have asked the AI assistant a few times in the last minute — give it a moment and press again.",
        AiQuotaVerdict.MemberDay => "You have used today's AI drafts — the allowance starts again after midnight UTC.",
        AiQuotaVerdict.Portal => "The daily AI call ceiling is reached — try again tomorrow.",
        AiQuotaVerdict.Budget => "The daily AI spend budget is reached — try again tomorrow.",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "An allowed call has no refusal."),
    };
}
