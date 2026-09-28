using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Services.Help;

namespace WinnersPortal.Api.Help;

[ApiController]
public sealed class HelpController : ControllerBase
{
    // Anonymous on purpose: this is user-facing documentation, and the
    // setup wizard needs it before any account exists. The payload is
    // static per deployment, so it is safe to cache hard.
    [HttpGet("api/help")]
    public IResult GetHelp()
    {
        Response.Headers.CacheControl = "public, max-age=300";
        return Results.Ok(new HelpResponse { Topics = HelpRegistry.All });
    }
}

/// <summary>Every help topic this build carries.</summary>
public sealed record HelpResponse
{
    public required IReadOnlyList<HelpTopic> Topics { get; init; }
}
