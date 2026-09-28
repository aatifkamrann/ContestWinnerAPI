using System.Security.Claims;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Auth;

/// <summary>
/// What a sign-in says about the account, as the claims a token carries.
/// The names are the JWT's own short ones (<c>sub</c>, <c>email</c>,
/// <c>name</c>, <c>role</c>), not the .NET URIs, so a token reads the same
/// to a consumer that is not this API — and the two portal-specific claims
/// are prefixed so they can never collide with a registered one.
/// </summary>
public static class Principal
{
    public const string SubjectClaim = "sub";
    public const string EmailClaim = "email";
    public const string NameClaim = "name";
    public const string RoleClaim = "role";

    /// <summary>The session stamp the token was issued with — see <see cref="User.SessionStamp"/>.</summary>
    public const string StampClaim = "wp:stamp";

    /// <summary>
    /// "1" on a token issued to a cookie the browser keeps across a close
    /// (the sign-in form's <em>Keep me signed in</em>), so a re-issue can
    /// carry the choice forward. Absent on bearer tokens.
    /// </summary>
    public const string PersistentClaim = "wp:persist";

    public const string AuthenticationType = "Bearer";

    public static ClaimsPrincipal For(User user, bool persistent = false) => new(Identity(user, persistent));

    public static ClaimsIdentity Identity(User user, bool persistent = false)
    {
        var identity = new ClaimsIdentity(AuthenticationType, NameClaim, RoleClaim);
        identity.AddClaim(new Claim(SubjectClaim, user.Id.ToString()));
        identity.AddClaim(new Claim(EmailClaim, user.Email));
        identity.AddClaim(new Claim(NameClaim, user.DisplayName));
        identity.AddClaim(new Claim(RoleClaim, user.Role));
        identity.AddClaim(new Claim(StampClaim, user.SessionStamp.ToString("N")));
        if (persistent) identity.AddClaim(new Claim(PersistentClaim, "1"));
        return identity;
    }

    /// <summary>The signed-in user's id, or null on an anonymous request.</summary>
    public static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(SubjectClaim), out var id) ? id : null;

    /// <summary>
    /// The role the token carries — one of <see cref="Roles"/>, or null on
    /// an anonymous request. What a member may read of somebody else's profile
    /// turns on it.
    /// </summary>
    public static string? Role(ClaimsPrincipal user) => user.FindFirstValue(RoleClaim);

    public static string? Email(ClaimsPrincipal user) => user.FindFirstValue(EmailClaim);

    public static string? Name(ClaimsPrincipal user) => user.FindFirstValue(NameClaim);

    /// <summary>
    /// The session stamp the token was issued with, compared against the
    /// row on every request; null on a token that carries none.
    /// </summary>
    public static Guid? Stamp(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(StampClaim), out var stamp) ? stamp : null;

    /// <summary>Whether the sign-in asked to be kept across a browser close.</summary>
    public static bool Persistent(ClaimsPrincipal user) => user.FindFirstValue(PersistentClaim) == "1";
}
