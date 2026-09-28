using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.GitHub;

namespace WinnersPortal.Api.GitHub;

/// <summary>The HTTP edge of <see cref="GitHubAuthService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class GitHubAuthController(GitHubAuthService githubAuth) : ControllerBase
{
    // Browser navigation, not fetch: redirects to GitHub's consent page.
    [HttpGet("api/github/connect")]
    [Authorize]
    public async Task<IResult> GetGithubConnect(string? next, CancellationToken ct) =>
        (await githubAuth.ConnectAsync(next, User, ct)).ToResult();

    [HttpGet("api/github/callback")]
    [Authorize]
    public async Task<IResult> GetGithubCallback(string? code, string? state, CancellationToken ct) =>
        (await githubAuth.CallbackAsync(code, state, User, ct)).ToResult();
}
