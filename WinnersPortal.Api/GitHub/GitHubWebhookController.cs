using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.GitHub;

namespace WinnersPortal.Api.GitHub;

/// <summary>The HTTP edge of <see cref="GitHubWebhookService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class GitHubWebhookController(GitHubWebhookService webhooks) : ControllerBase
{
    // GitHub signs the raw body, so it is read here as bytes, never bound.
    [HttpPost("api/webhooks/github")]
    public async Task<IResult> PostWebhooksGithub(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        return (await webhooks.ReceiveAsync(
            buffer.ToArray(),
            Request.Headers["X-Hub-Signature-256"],
            Request.Headers["X-GitHub-Event"].ToString(),
            Request.Headers["X-GitHub-Delivery"].ToString(),
            ct)).ToResult();
    }
}
