using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Domain;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Api.Ai;

/// <summary>The HTTP edge of <see cref="AiService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class AiController(AiService aiService) : ControllerBase
{
    // What the UI may offer this viewer — panels for switched-off
    // features must not render at all.
    [HttpGet("api/ai/status")]
    [Authorize]
    public async Task<IResult> GetAiStatus([FromServices] AiOptions ai, CancellationToken ct)
    {
        return Results.Ok(new AiStatusResponse
        {
            Enabled = await ai.IsEnabledAsync(ct),
            SendCode = await ai.MaySendCodeAsync(ct),
            Features = new AiFeatureSwitches
            {
                Milestones = await ai.IsFeatureEnabledAsync(AiFeature.MilestoneExtraction, ct),
                Coach = await ai.IsFeatureEnabledAsync(AiFeature.BriefCoach, ct),
                Digest = await ai.IsFeatureEnabledAsync(AiFeature.EntryDigest, ct),
                Spam = await ai.IsFeatureEnabledAsync(AiFeature.SpamFilter, ct),
                Narrative = await ai.IsFeatureEnabledAsync(AiFeature.ProgressNarrative, ct),
                Seo = await ai.IsFeatureEnabledAsync(AiFeature.SeoMetadata, ct),
                Standing = await ai.IsFeatureEnabledAsync(AiFeature.StandingNotes, ct),
                Categorise = await ai.IsFeatureEnabledAsync(AiFeature.CategorySuggestion, ct),
                Recommended = await ai.IsFeatureEnabledAsync(AiFeature.RecommendedMatching, ct),
                Summary = await ai.IsFeatureEnabledAsync(AiFeature.ProfileSummary, ct),
                Review = await ai.IsFeatureEnabledAsync(AiFeature.ProfileReview, ct),
                Approach = await ai.IsFeatureEnabledAsync(AiFeature.ProjectApproach, ct),
                Evaluation = await ai.IsFeatureEnabledAsync(AiFeature.ApplicationEvaluation, ct),
                Requirements = await ai.IsFeatureEnabledAsync(AiFeature.RequirementsSuggestion, ct),
                Criteria = await ai.IsFeatureEnabledAsync(AiFeature.CriteriaSuggestion, ct),
            },
        });
    }

    // --------------------------------- the tools that read the form
    // Everything else here queues a job, because everything else reads
    // something the portal already stores. These six read the opportunity
    // form on screen: the whole form travels in the request, so they
    // work while an opportunity is being posted, before there is any row to
    // be about — and they answer what the client is looking at rather
    // than what they last saved. Each is handed the sections above the
    // one it fills (AiFormReads), so a draft builds on what the client
    // has already decided.
    //
    // Answered inline rather than queued because somebody is waiting for
    // each of them with their hand on the field it fills.
    [HttpPost("api/opportunities/ai/{feature}")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostOpportunitiesAi(string feature, OpportunityFormSnapshot request, CancellationToken ct) =>
        (await aiService.DraftBriefFieldAsync(feature, request, ct)).ToResult();

    // The one tool that reads the profile form: a first draft of the
    // member's About, from what they have typed so far — title, skills,
    // languages, past work, and the words they already have. Any member:
    // a client's profile has an About too. The details travel in the
    // request for the same reason the opportunity tools' do — it reads the
    // form on screen, saved or not — and in a shape of their own that
    // cannot carry payment details (ProfileSummaryRequest).
    [HttpPost("api/profile/ai/summary")]
    [Authorize]
    public async Task<IResult> PostProfileAiSummary(ProfileSummaryRequest request, CancellationToken ct) =>
        (await aiService.DraftProfileSummaryAsync(request, User, ct)).ToResult();

    // ------------------------------------------- the profile review
    // The box on a freelancer's own preview: what their profile is
    // strong for, and what would make more of the open opportunities theirs
    // to enter. The lines and their figures are arithmetic and are on
    // the page at once; the model's words for them are queued from
    // here, the way the Recommended reading is queued from a browse —
    // nobody presses anything for it, so the read is the one trigger
    // that cannot be missed. The input is hashed: an unchanged profile
    // against unchanged opportunities costs nothing, and a changed one gets
    // fresh words while the portal's own stand in.
    [HttpGet("api/profile/ai/review")]
    [Authorize(Policy = "freelancer")]
    public async Task<IResult> GetProfileAiReview(CancellationToken ct) =>
        (await aiService.ProfileReviewAsync(User, ct)).ToResult();

    // ------------------------------------- opportunity-scoped draft requests
    [HttpPost("api/opportunities/{id:guid}/ai/{feature}")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostOpportunitiesAiByIdGuidByFeature(Guid id, string feature, CancellationToken ct) =>
        (await aiService.RequestOpportunityFeatureAsync(id, feature, User, ct)).ToResult();

    [HttpGet("api/opportunities/{id:guid}/ai/{feature}")]
    [Authorize(Policy = "client")]
    public async Task<IResult> GetOpportunitiesAi(Guid id, string feature, CancellationToken ct) =>
        (await aiService.OpportunityFeatureAsync(id, feature, User, ct)).ToResult();

    // ------------------------------------------------- entry digests
    // Addressed by entry, readable by the opportunity's client only — the
    // digest is the reviewer's reading aid, never the entrant's scorecard.
    [HttpPost("api/entries/{id:guid}/ai/digest")]
    [Authorize(Policy = "client")]
    public async Task<IResult> PostEntriesAiDigest(Guid id, CancellationToken ct) =>
        (await aiService.RequestDigestAsync(id, User, ct)).ToResult();

    [HttpGet("api/entries/{id:guid}/ai/digest")]
    [Authorize(Policy = "client")]
    public async Task<IResult> GetEntriesAiDigest(Guid id, CancellationToken ct) =>
        (await aiService.DigestAsync(id, User, ct)).ToResult();

    [HttpPost("api/opportunities/{id:guid}/ai/standing")]
    [Authorize]
    public async Task<IResult> PostOpportunitiesAiStanding(Guid id, CancellationToken ct) =>
        (await aiService.RequestStandingNotesAsync(id, User, ct)).ToResult();

    [HttpGet("api/opportunities/{id:guid}/ai/standing")]
    [Authorize]
    public async Task<IResult> GetOpportunitiesAiStanding(Guid id, CancellationToken ct) =>
        (await aiService.StandingNotesAsync(id, User, ct)).ToResult();
}

/// <summary>What the UI may offer: AI at all, whether code may be sent, and each feature's switch.</summary>
public sealed record AiStatusResponse
{
    public required bool Enabled { get; init; }
    public required bool SendCode { get; init; }
    public required AiFeatureSwitches Features { get; init; }
}

/// <summary>Each AI feature's switch, under the name the UI reads it by.</summary>
public sealed record AiFeatureSwitches
{
    public required bool Milestones { get; init; }
    public required bool Coach { get; init; }
    public required bool Digest { get; init; }
    public required bool Spam { get; init; }
    public required bool Narrative { get; init; }
    public required bool Seo { get; init; }
    public required bool Standing { get; init; }
    public required bool Categorise { get; init; }
    public required bool Recommended { get; init; }
    public required bool Summary { get; init; }
    public required bool Review { get; init; }
    public required bool Approach { get; init; }
    public required bool Evaluation { get; init; }
    public required bool Requirements { get; init; }
    public required bool Criteria { get; init; }
}
