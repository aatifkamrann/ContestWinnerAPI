using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.Live;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// Rating an award, both directions: the winner scores the client, the client
/// scores the winner. Who the rating is *about* is derived from who is asking
/// — never posted, so a request cannot rate anyone it likes.
/// </summary>
public sealed class RatingService(AppDbContext db, EmailWorkSignal emailSignal, ILiveBoard live)
{
    public async Task<Outcome<RatingResponse>> RateAsync(Guid id, RateRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal)!.Value;
        var award = await db.Awards
            .Include(a => a.Opportunity).ThenInclude(c => c!.Client)
            .Include(a => a.Entry).ThenInclude(e => e!.Freelancer)
            .SingleOrDefaultAsync(a => a.Id == id, ct);

        // A stranger gets the same 404 as a missing award — the money
        // trail is the parties' business until the rating itself is public.
        var isClient = award is not null && award.Opportunity!.ClientId == viewerId;
        var isWinner = award is not null && award.Entry!.FreelancerId == viewerId;
        if (award is null || (!isClient && !isWinner)) return Outcome.NotFound();

        if (award.PaidAtUtc is null)
            return Outcome.Conflict(
                "Ratings open once the award is marked paid — the deal has to finish before either side scores it.");

        if (RatingRules.Problem(request.Stars, request.Comment) is { } problem)
            return Outcome.Invalid(problem);
        var comment = RatingRules.CleanComment(request.Comment);

        var about = isClient ? award.Entry!.Freelancer! : award.Opportunity!.Client!;
        var now = DateTimeOffset.UtcNow;
        var rating = await db.Ratings
            .SingleOrDefaultAsync(r => r.AwardId == award.Id && r.ByUserId == viewerId, ct);
        var isNew = rating is null;
        if (rating is null)
        {
            rating = new Rating
            {
                Id = Guid.NewGuid(),
                AwardId = award.Id,
                ByUserId = viewerId,
                OfUserId = about.Id,
                CreatedAtUtc = now,
            };
            db.Ratings.Add(rating);
        }
        rating.Stars = request.Stars;
        rating.Comment = comment;
        rating.UpdatedAtUtc = now;

        // The rated party hears about it once per award: only the first
        // save queues mail — an edit is the same opinion, revised — and
        // the dedupe key backstops a race of two first saves.
        if (isNew)
        {
            var by = isClient ? award.Opportunity!.Client! : award.Entry!.Freelancer!;
            Notify.Queue(db, about, "rating_received",
                Emails.RatingReceived(award.Opportunity!.Title, award.Opportunity.Slug, request.Stars, by.DisplayName),
                dedupeKey: $"rating:{award.Id:N}:{viewerId:N}");
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            // Two submits raced; the first one's row exists now. The
            // caller retries as an edit — same request, same button.
            return Outcome.Conflict("Your rating just saved from another tab — reload to edit it.");
        }

        // New or revised, the rated party's average just moved.
        await Recount.UserAsync(db, about.Id, ct);
        if (isNew) emailSignal.Wake();
        // New or revised, the public words under the winner banner changed.
        await live.OpportunityChangedAsync(award.Opportunity!.Slug, ct);
        return Outcome.Ok(new RatingResponse
        {
            Stars = rating.Stars,
            Comment = rating.Comment,
            UpdatedAtUtc = rating.UpdatedAtUtc,
        });
    }
}

public sealed record RateRequest(int Stars, string? Comment);
