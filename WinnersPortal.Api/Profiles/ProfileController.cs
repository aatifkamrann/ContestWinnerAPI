using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Auth;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Api.Profiles;

/// <summary>The HTTP edge of <see cref="ProfileService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class ProfileController(ProfileService profiles) : ControllerBase
{
    // --------------------------------------------------------- picture
    // Public, like the name it sits beside: the entrant list on an
    // opportunity page is readable signed out, and a picture that needed a
    // cookie would be a broken image there. The URL carries the version
    // (AvatarRules.Url), so a browser may keep a picture for a year — a
    // new picture is a new address. Without the version it is served
    // fresh each time, for anybody who typed the bare path.
    [HttpGet("api/avatars/{id:guid}")]
    public async Task<IResult> GetAvatars(Guid id, CancellationToken ct)
    {
        var outcome = await profiles.AvatarAsync(id, ct);
        if (outcome.Kind == OutcomeKind.File)
            Response.Headers.CacheControl = Request.Query.ContainsKey("v")
                ? "public, max-age=31536000, immutable"
                : "no-cache";
        return outcome.ToResult();
    }

    // ------------------------------------- the pictures of past work
    // Public for the same reason the avatars above are: these render on
    // a page the browser has already been allowed to open, and a picture
    // that needed a cookie of its own would be a broken image on it. The
    // id is the whole address — a replaced picture is a new row with a
    // new id, never the same one holding different bytes — so there is
    // no version to carry and a browser may keep one for a year.
    //
    // A picture belonging to a project its owner has not published is
    // still served to anybody holding the id. The id is 128 random bits
    // that appear in the owner's own copy of their profile and nowhere
    // else: it is not a list anybody can walk, and the row is gone the
    // moment they take the picture off the form.
    [HttpGet("api/project-images/{id:guid}")]
    public async Task<IResult> GetProjectImages(Guid id, CancellationToken ct)
    {
        var outcome = await profiles.ProjectImageAsync(id, ct);
        if (outcome.Kind == OutcomeKind.File)
            Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return outcome.ToResult();
    }

    // ------------------------------------------------------------ mine
    [HttpGet("api/profile")]
    [Authorize]
    public async Task<IResult> GetProfile(CancellationToken ct) =>
        (await profiles.OwnAsync(User, ct)).ToResult();

    // ------------------------------------------------- the welcome
    // The screen after the last step of joining: where the member now
    // stands, read fresh each time (Welcome.cs). Their own, like the
    // checklist — a rank is public arithmetic on everybody's score,
    // but the page it is on is theirs.
    [HttpGet("api/profile/welcome")]
    [Authorize]
    public async Task<IResult> GetWelcome(CancellationToken ct) =>
        (await profiles.WelcomeAsync(User, ct)).ToResult();

    [HttpPut("api/profile")]
    [Authorize]
    public async Task<IResult> PutProfile(ProfileRequest request, [FromServices] TokenService tokens, CancellationToken ct)
    {
        var outcome = await profiles.SaveAsync(request, User, ct);
        SessionCookie.Apply(HttpContext, tokens, outcome);
        return outcome.ToResult();
    }

    // -------------------------------------------------- payment methods
    // The catalogue the form builds itself from: every way of being paid
    // this portal knows to ask about, and the fields each one needs.
    // Nothing here is anybody's data — it is the shape of the questions.
    [HttpGet("api/profile/payment-methods")]
    [Authorize]
    public IResult GetPaymentMethods() =>
        Results.Ok(new PaymentMethodsResponse
    {
        Methods = PaymentMethods.All.Select(m => new PaymentMethodOption
        {
            Key = m.Key,
            Label = m.Label,
            Hint = m.Hint,
            Identity = m.Identity,
            Fields = m.Fields.Select(f => new PaymentFieldOption
            {
                Key = f.Key,
                Label = f.Label,
                Placeholder = f.Placeholder,
                Required = f.Required,
                MaxLength = f.MaxLength,
            }),
        }),
        Max = ProfileRules.MaxPayments,
    });

    // ------------------------------------------------------ suggestions
    // What other members already wrote in the free-text fields, so the
    // form can offer it back and the same city or skill is spelt one
    // way across the portal. Nothing here is a validation list — what
    // a member types stays as typed — and nothing here is private:
    // every value is already on a profile page any member can open,
    // which is exactly why no payment field is among them.
    [HttpGet("api/profile/suggestions")]
    [Authorize]
    public async Task<IResult> GetSuggestions(CancellationToken ct) =>
        (await profiles.SuggestionsAsync(ct)).ToResult();

    // ------------------------------------------------------- somebody's
    // Members-only, which the opportunity page beside it no longer is: a
    // brief is what somebody decides to join over, and a portfolio is
    // part of what an entrant brings to one — a conversation between
    // members rather than a public directory of people.
    [HttpGet("api/profile/{userId:guid}")]
    [Authorize]
    public async Task<IResult> GetProfileByUserIdGuid(Guid userId, CancellationToken ct) =>
        (await profiles.MemberAsync(userId, User, ct)).ToResult();
}

/// <summary>Every way of being paid the profile form can offer, and how many rows a profile may hold.</summary>
public sealed record PaymentMethodsResponse
{
    public required IEnumerable<PaymentMethodOption> Methods { get; init; }
    public required int Max { get; init; }
}

/// <summary>One way of being paid: what it is called, the hint under it, the field that names the account, and its fields.</summary>
public sealed record PaymentMethodOption
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Hint { get; init; }
    public required string Identity { get; init; }
    public required IEnumerable<PaymentFieldOption> Fields { get; init; }
}

/// <summary>One box on a payment method's row.</summary>
public sealed record PaymentFieldOption
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Placeholder { get; init; }
    public required bool Required { get; init; }
    public required int MaxLength { get; init; }
}
