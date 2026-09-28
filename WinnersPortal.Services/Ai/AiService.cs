using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// The request side of the AI layer. A POST only queues an artifact and
/// wakes the worker — no page ever waits on a model, and no page breaks
/// because one was unavailable. Every draft is fetched back by the person
/// who asked for it, read, and acted on by them: AI drafts, humans decide.
/// </summary>
public sealed partial class AiService(AiOptions ai, AiQuota quota, AiProviderClient providerClient, ILogger<AiService.AiFormDraftLog> log, AppDbContext db, AiWorkSignal aiSignal, GitHubService github)
{
    public async Task<Outcome<AiDraftResponse>> DraftBriefFieldAsync(string feature, OpportunityFormSnapshot request, CancellationToken ct)
    {
        if (AiFeatureRoutes.FromFormSlug(feature) is not { } f) return Outcome.NotFound();

        if (await GateProblemAsync(ai, f, ct) is { } gateProblem)
            return Outcome.Conflict(gateProblem);

        // A press on an empty form must not cost anything: there is
        // nothing to read, and the model would answer anyway. The title
        // and the brief are the measure, whatever else is filled in — a
        // category over an empty brief is not something to draft from.
        if (AiInputs.Substance(request) < MinTextToRead)
            return Outcome.Invalid(
                "Write the title and a few lines of the brief first — there is nothing to read yet.");

        return await DraftInlineAsync(
            f, AiPrompts.ForForm(f, request), ai, quota, providerClient, log, ct);
    }

    public async Task<Outcome<AiDraftResponse>> DraftProfileSummaryAsync(ProfileSummaryRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        const AiFeature f = AiFeature.ProfileSummary;
        if (await GateProblemAsync(ai, f, ct) is { } gateProblem)
            return Outcome.Conflict(gateProblem);

        var (json, substance) = AiInputs.ProfileSummary(request, Principal.Role(principal));
        if (substance < MinTextToRead)
            return Outcome.Invalid(
                "Add a title, a few skills or a line about your work first — there is nothing to read yet.");

        return await DraftInlineAsync(f, AiPrompts.ProfileSummary(json), ai, quota, providerClient, log, ct);
    }

    public async Task<Outcome<ProfileReviewResponse>> ProfileReviewAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        const AiFeature f = AiFeature.ProfileReview;
        var userId = Principal.UserId(principal);
        if (userId is null) return Outcome.Unauthorized();
        if (await GateProblemAsync(ai, f, ct) is { } gateProblem)
            return Outcome.Ok<ProfileReviewResponse>(new ProfileReviewSkipped { Status = "skipped", Note = gateProblem });

        var facts = await ProfileReview.ReadAsync(db, userId.Value, await github.CanConnectAsync(ct), ct);
        if (facts is null) return Outcome.NotFound();
        var hash = AiRules.InputHash(AiInputs.ProfileReview(facts));

        var artifact = await ArtifactAsync(db, f, userId.Value, ct);
        if (artifact?.InputHash != hash && artifact?.Status != AiArtifactStatus.Pending)
        {
            artifact = await UpsertAsync(db, f, userId.Value, ct);
            aiSignal.Wake();
        }
        return Outcome.Ok<ProfileReviewResponse>(ReviewDto(artifact!, facts));
    }

    public async Task<Outcome<AiArtifactView>> RequestOpportunityFeatureAsync(Guid id, string feature, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (AiFeatureRoutes.FromSlug(feature) is not { } f) return Outcome.NotFound();

        var opportunity = await OwnedAsync(db, id, Principal.UserId(principal), ct);
        if (opportunity is null) return Outcome.NotFound();

        if (await GateProblemAsync(ai, f, ct) is { } gateProblem)
            return Outcome.Conflict(gateProblem);

        // The scan reads entries, which only a published opportunity has.
        if (opportunity.Status is not (OpportunityStatus.Open or OpportunityStatus.Reviewing))
            return Outcome.Conflict("The scan reads entries — it runs while an opportunity is open or in review.");
        if (!opportunity.HasEntries)
            return Outcome.Conflict("There are no active entries to scan yet.");

        var artifact = await UpsertAsync(db, f, id, ct);
        aiSignal.Wake();
        return Outcome.Ok(Dto(artifact));
    }

    public async Task<Outcome<AiArtifactResponse>> OpportunityFeatureAsync(Guid id, string feature, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (AiFeatureRoutes.FromSlug(feature) is not { } f) return Outcome.NotFound();
        var owns = await OwnsOpportunityAsync(db, id, Principal.UserId(principal), ct);
        if (!owns) return Outcome.NotFound();

        var artifact = await ArtifactAsync(db, f, id, ct);
        return Outcome.Ok<AiArtifactResponse>(artifact is null ? new AiArtifactNone { Status = "none" } : Dto(artifact));
    }

    public async Task<Outcome<AiArtifactView>> RequestDigestAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var entry = await EntryGateAsync(db, id, Principal.UserId(principal), ct);
        if (entry is null) return Outcome.NotFound();
        if (entry.Status != EntryStatus.Active || entry.FrozenAtUtc is null)
            return Outcome.Conflict("Digests describe frozen work — they become available when the deadline freezes this entry.");

        if (await GateProblemAsync(ai, AiFeature.EntryDigest, ct) is { } gateProblem)
            return Outcome.Conflict(gateProblem);

        var artifact = await UpsertAsync(db, AiFeature.EntryDigest, id, ct);
        aiSignal.Wake();
        return Outcome.Ok(Dto(artifact));
    }

    public async Task<Outcome<AiArtifactResponse>> DigestAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var owns = await OwnsEntryAsync(db, id, Principal.UserId(principal), ct);
        if (!owns) return Outcome.NotFound();

        var artifact = await ArtifactAsync(db, AiFeature.EntryDigest, id, ct);
        return Outcome.Ok<AiArtifactResponse>(artifact is null ? new AiArtifactNone { Status = "none" } : Dto(artifact));
    }

    public async Task<Outcome<AiArtifactView>> RequestStandingNotesAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var opportunity = await ModeratedAsync(db, id, principal, ct);
        if (opportunity is null) return Outcome.NotFound();

        if (await GateProblemAsync(ai, AiFeature.StandingNotes, ct) is { } gateProblem)
            return Outcome.Conflict(gateProblem);
        if (opportunity.Status is not (OpportunityStatus.Open or OpportunityStatus.Reviewing))
            return Outcome.Conflict("Standing notes read a live board — they run while an opportunity is open or in review.");
        if (!opportunity.HasEntries)
            return Outcome.Conflict("There are no active entrants to read yet.");

        var artifact = await UpsertAsync(db, AiFeature.StandingNotes, id, ct);
        aiSignal.Wake();
        return Outcome.Ok(Dto(artifact));
    }

    public async Task<Outcome<AiArtifactResponse>> StandingNotesAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (await ModeratedAsync(db, id, principal, ct) is null) return Outcome.NotFound();

        var artifact = await ArtifactAsync(db, AiFeature.StandingNotes, id, ct);
        return Outcome.Ok<AiArtifactResponse>(artifact is null ? new AiArtifactNone { Status = "none" } : Dto(artifact));
    }

    /// <summary>
    /// Maps the standing-notes pair. Owner or administrator: the two people
    /// who can take an entrant off, reading the same notes beside the same
    /// button. Entrants get their own standing on the opportunity page — the
    /// number and its arithmetic — never these notes, which are the client's
    /// reading aid for a decision about them.
    /// </summary>

    /// <summary>Marker type, so the inline drafts log under a name of their own.</summary>
    public sealed class AiFormDraftLog;

    /// <summary>Below this much text there is nothing worth spending a call on.</summary>
    private const int MinTextToRead = 30;

    internal sealed record ModeratedOpportunity(OpportunityStatus Status, bool HasEntries);

    /// <summary>An entry as the digest's door reads it: its state, and when it froze.</summary>
    internal sealed record EntryGate(EntryStatus Status, DateTimeOffset? FrozenAtUtc);

    // ---- the reads, in the LINQ or the T-SQL (AiService.SqlServer.cs)

    /// <summary>The opportunity, if this viewer owns it or administers the portal; null reads as not found.</summary>
    private static Task<ModeratedOpportunity?> ModeratedAsync(
        AppDbContext db, Guid id, ClaimsPrincipal user, CancellationToken ct) =>
        ModeratedAsync(db, id, Principal.UserId(user), user.IsInRole(Roles.Admin), ct);

    /// <summary>The opportunity, if this viewer owns it; null reads as not found.</summary>
    private static Task<ModeratedOpportunity?> OwnedAsync(AppDbContext db, Guid id, Guid? clientId, CancellationToken ct) =>
        ModeratedAsync(db, id, clientId, isAdmin: false, ct);

    private static Task<ModeratedOpportunity?> ModeratedAsync(
        AppDbContext db, Guid id, Guid? viewerId, bool isAdmin, CancellationToken ct) =>
        db.UseDapper
            ? ModeratedSqlAsync(db.Sql, id, viewerId, isAdmin, ct)
            : ModeratedLinqAsync(db, id, viewerId, isAdmin, ct);

    internal static Task<ModeratedOpportunity?> ModeratedLinqAsync(
        AppDbContext db, Guid id, Guid? viewerId, bool isAdmin, CancellationToken ct) =>
        db.Opportunities.AsNoTracking()
            .Where(c => c.Id == id && (isAdmin || c.ClientId == viewerId))
            .Select(c => new ModeratedOpportunity(c.Status, c.Entries.Any(e => e.Status == EntryStatus.Active)))
            .SingleOrDefaultAsync(ct);

    /// <summary>The stored artifact for a feature and subject, read and not tracked.</summary>
    private static Task<AiArtifact?> ArtifactAsync(AppDbContext db, AiFeature feature, Guid subjectId, CancellationToken ct) =>
        db.UseDapper
            ? ArtifactSqlAsync(db.Sql, feature, subjectId, ct)
            : db.AiArtifacts.AsNoTracking().SingleOrDefaultAsync(a => a.Feature == feature && a.SubjectId == subjectId, ct);

    private static Task<bool> OwnsOpportunityAsync(AppDbContext db, Guid id, Guid? clientId, CancellationToken ct) =>
        db.UseDapper
            ? OwnsOpportunitySqlAsync(db.Sql, id, clientId, ct)
            : db.Opportunities.AnyAsync(c => c.Id == id && c.ClientId == clientId, ct);

    private static Task<EntryGate?> EntryGateAsync(AppDbContext db, Guid id, Guid? clientId, CancellationToken ct) =>
        db.UseDapper
            ? EntryGateSqlAsync(db.Sql, id, clientId, ct)
            : EntryGateLinqAsync(db, id, clientId, ct);

    internal static Task<EntryGate?> EntryGateLinqAsync(AppDbContext db, Guid id, Guid? clientId, CancellationToken ct) =>
        db.Entries.AsNoTracking()
            .Where(e => e.Id == id && e.Opportunity!.ClientId == clientId)
            .Select(e => new EntryGate(e.Status, e.FrozenAtUtc))
            .SingleOrDefaultAsync(ct);

    private static Task<bool> OwnsEntryAsync(AppDbContext db, Guid id, Guid? clientId, CancellationToken ct) =>
        db.UseDapper
            ? OwnsEntrySqlAsync(db.Sql, id, clientId, ct)
            : db.Entries.AnyAsync(e => e.Id == id && e.Opportunity!.ClientId == clientId, ct);

    /// <summary>Why a request may not run right now, in the requester's words — or null when it may.</summary>
    internal static async Task<string?> GateProblemAsync(AiOptions ai, AiFeature feature, CancellationToken ct)
    {
        if (await ai.IsFeatureEnabledAsync(feature, ct)) return null;
        if (!await ai.IsEnabledAsync(ct))
            return "AI automation is switched off on this portal.";
        if (AiOptions.RequiresProvider(feature) && await ai.ProviderConfigAsync(ct) is null)
            return "No AI provider key is saved — the operator can add one in settings.";
        return "This AI feature is switched off on this portal.";
    }

    /// <summary>
    /// One call, answered inside the request, for the tools that read a
    /// form. The quota is consumed before the call and given back when the
    /// provider refused to run it (a 429 or a 5xx costs nothing; a 4xx was
    /// ours), and the answer goes through the same validation every
    /// queued draft does, so nothing off-contract reaches a form.
    /// </summary>
    internal static async Task<Outcome<AiDraftResponse>> DraftInlineAsync(
        AiFeature f, (string System, string User) prompt,
        AiOptions ai, AiQuota quota, AiProviderClient providerClient, ILogger log, CancellationToken ct)
    {
        if (!await quota.TryConsumeAsync(ct))
            return Outcome.Conflict("The daily AI call ceiling is reached — try again tomorrow.");

        var config = await ai.ProviderConfigAsync(ct);
        if (config is null)
            return Outcome.Conflict("No AI provider key is saved — the operator can add one in settings.");
        var model = config.Model ?? AiProviderRequests.DefaultModel(config.Provider);

        string answer;
        try
        {
            answer = await providerClient.CompleteAsync(
                config.Provider, model, config.ApiKey, prompt.System, prompt.User, ct);
        }
        catch (AiProviderException e)
        {
            // Nothing ran, so nothing was spent: give the counted call back
            // rather than charging the member for the provider having a bad
            // minute. They can press again straight away.
            if (e.StatusCode is 429 or >= 500) await quota.RefundAsync(ct);
            log.LogWarning(e, "AI {Feature} refused by {Provider}.", f, config.Provider);
            return Outcome.Conflict(e.Friendly);
        }

        var canonical = AiOutputs.Validate(f, answer, out var error);
        if (canonical is null)
            return Outcome.Conflict(error ?? "The answer did not match the expected shape.");

        log.LogInformation("AI {Feature} drafted inline by {Provider}:{Model}.", f, config.Provider, model);
        return Outcome.Ok(new AiDraftResponse
        {
            Output = JsonSerializer.Deserialize<JsonElement>(canonical),
            Provider = AiBrand.Name,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// The review as the page gets it: the live lines with the live figures,
    /// worded by the model where its last answer has words for them and by
    /// the portal otherwise. "worded" says whether any of the words are the
    /// model's; "stale" that they answer an older profile than this one.
    /// </summary>
    private static ProfileReviewResult ReviewDto(AiArtifact a, ProfileReview.Facts facts) => new ProfileReviewResult
    {
        Status = a.Status switch
        {
            AiArtifactStatus.Pending => "pending",
            AiArtifactStatus.Done => "done",
            AiArtifactStatus.Failed => "failed",
            _ => "skipped",
        },
        Output = ProfileReview.Resolve(a.OutputJson, facts),
        OpenOpportunities = facts.OpenOpportunities,
        Worded = a.OutputJson is not null,
        Stale = a.OutputJson is not null && a.Status != AiArtifactStatus.Done,
        Note = a.Note,
        Provider = AiBrand.Public(a.Provider),
        CompletedAtUtc = a.CompletedAtUtc,
    };

    private static async Task<AiArtifact> UpsertAsync(
        AppDbContext db, AiFeature feature, Guid subjectId, CancellationToken ct)
    {
        await AiWorker.QueueAsync(db, feature, subjectId, DateTimeOffset.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        return await db.AiArtifacts.AsNoTracking()
            .SingleAsync(a => a.Feature == feature && a.SubjectId == subjectId, ct);
    }

    /// <summary>Output only travels once it is Done — a re-queued draft shows as pending, not stale.</summary>
    internal static AiArtifactView Dto(AiArtifact a) => new AiArtifactView
    {
        Status = a.Status switch
        {
            AiArtifactStatus.Pending => "pending",
            AiArtifactStatus.Done => "done",
            AiArtifactStatus.Failed => "failed",
            _ => "skipped",
        },
        Output = a.Status == AiArtifactStatus.Done && a.OutputJson is not null
            ? JsonSerializer.Deserialize<JsonElement>(a.OutputJson)
            : (JsonElement?)null,
        Note = a.Note,
        Provider = AiBrand.Public(a.Provider),
        CompletedAtUtc = a.CompletedAtUtc,
    };
}
