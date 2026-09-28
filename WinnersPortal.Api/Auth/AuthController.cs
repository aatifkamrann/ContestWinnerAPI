using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Auth;

namespace WinnersPortal.Api.Auth;

/// <summary>The HTTP edge of <see cref="AuthService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    // Open registration for the two public roles. Administrators are only
    // ever created by the setup wizard, never through this endpoint.
    [HttpPost("api/auth/register")]
    public async Task<IResult> PostRegister(RegisterRequest request, [FromServices] TokenService tokens, CancellationToken ct)
    {
        var outcome = await auth.RegisterAsync(request, ct);
        SessionCookie.Apply(HttpContext, tokens, outcome);
        return outcome.ToResult();
    }

    // ---------------------------------------------- confirming contact
    // The code back. Either channel's code confirms; the row records
    // which. Five wrong guesses spend the pair — a six-digit code is
    // only as safe as the number of tries it gets.
    [HttpPost("api/auth/confirm")]
    [Authorize]
    public async Task<IResult> PostConfirm(ConfirmRequest request, CancellationToken ct) =>
        (await auth.ConfirmAsync(request, User, ct)).ToResult();

    // A fresh pair, once a minute. Both channels again, not only the one
    // that failed: the person cannot tell which did.
    [HttpPost("api/auth/resend-code")]
    [Authorize]
    public async Task<IResult> PostResendCode(CancellationToken ct) =>
        (await auth.ResendCodeAsync(User, ct)).ToResult();

    [HttpPost("api/auth/login")]
    public async Task<IResult> PostLogin(LoginRequest request, [FromServices] TokenService tokens, CancellationToken ct)
    {
        var outcome = await auth.LoginAsync(request, ct);
        SessionCookie.Apply(HttpContext, tokens, outcome);
        return outcome.ToResult();
    }

    [HttpPost("api/auth/logout")]
    public IResult PostLogout()
    {
        SessionCookie.Clear(HttpContext);
        return Results.NoContent();
    }

    // ------------------------------------------------- bearer tokens
    // The same door as login for a client that holds no cookie: a
    // script, a mobile app, another service. It gets an hour-long
    // access token to send as `Authorization: Bearer`, and a refresh
    // token to exchange for the next pair. The refresh token is
    // single-use — every exchange spends it and issues a successor —
    // and a spent one presented again ends every token the account
    // holds, since two parties evidently have it.
    [HttpPost("api/auth/token")]
    public async Task<IResult> PostToken(LoginRequest request, CancellationToken ct) =>
        (await auth.IssueTokenAsync(request, ct)).ToResult();

    [HttpPost("api/auth/token/refresh")]
    public async Task<IResult> PostTokenRefresh(RefreshRequest request, CancellationToken ct) =>
        (await auth.RefreshTokenAsync(request, ct)).ToResult();

    // Sign-out for a bearer client: the refresh token is withdrawn, so
    // the hour the access token has left is all that remains of the
    // session. Answers 204 whether or not the token was known.
    [HttpPost("api/auth/token/revoke")]
    public async Task<IResult> PostTokenRevoke(RefreshRequest request, CancellationToken ct) =>
        (await auth.RevokeTokenAsync(request, ct)).ToResult();

    // ------------------------------------------------ forgot password
    // Two doors. The first takes an address and always answers 204:
    // whether that address has an account is not something to tell a
    // stranger, and the wording of the email assumes nothing either. The
    // token and its email ride the same save, so a link can never exist
    // that the row does not know about.
    [HttpPost("api/auth/forgot-password")]
    public async Task<IResult> PostForgotPassword(ForgotPasswordRequest request, CancellationToken ct) =>
        (await auth.ForgotPasswordAsync(request, ct)).ToResult();

    // The second takes the token back with the new password. The password
    // is checked first, so a typo there does not spend the link.
    [HttpPost("api/auth/reset-password")]
    public async Task<IResult> PostResetPassword(ResetPasswordRequest request, [FromServices] TokenService tokens, CancellationToken ct)
    {
        var outcome = await auth.ResetPasswordAsync(request, ct);
        SessionCookie.Apply(HttpContext, tokens, outcome);
        return outcome.ToResult();
    }

    // Signed in and changing it: the current password again, then the new
    // one. Other sessions end; this cookie is re-issued on the new stamp.
    [HttpPost("api/auth/change-password")]
    [Authorize]
    public async Task<IResult> PostChangePassword(ChangePasswordRequest request, [FromServices] TokenService tokens, CancellationToken ct)
    {
        var outcome = await auth.ChangePasswordAsync(request, User, ct);
        SessionCookie.Apply(HttpContext, tokens, outcome);
        return outcome.ToResult();
    }

    // ------------------------------------------------- per-user theme
    // As implemented in BookingApp: the signed-in user's accent colour,
    // and their badge colour per opportunity status, persisted on the user
    // row so both follow them across devices. Light/dark MODE is
    // intentionally not here — it stays a per-device preference the
    // client holds locally.
    [HttpGet("api/auth/theme")]
    [Authorize]
    public async Task<IResult> GetTheme(CancellationToken ct) =>
        (await auth.ThemeAsync(User, ct)).ToResult();

    [HttpPut("api/auth/theme")]
    [Authorize]
    public async Task<IResult> PutTheme(UserThemeRequest request, CancellationToken ct) =>
        (await auth.SaveThemeAsync(request, User, ct)).ToResult();

    // Reads the row, not just the claims: the connected-GitHub state has
    // to be visible the moment the OAuth callback redirects back.
    [HttpGet("api/auth/me")]
    [Authorize]
    public async Task<IResult> GetMe(CancellationToken ct) =>
        (await auth.MeAsync(User, ct)).ToResult();

    // Records the acceptance against the version that stands now, on
    // the account. Never lowers what is recorded: a version the operator
    // later reduces has still been agreed to. One of the two writes the
    // terms gate lets through — the other is signing out.
    [HttpPost("api/auth/accept-terms")]
    [Authorize]
    public async Task<IResult> PostAcceptTerms(CancellationToken ct) =>
        (await auth.AcceptTermsAsync(User, ct)).ToResult();
}
