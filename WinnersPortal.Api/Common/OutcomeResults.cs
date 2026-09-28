using Microsoft.Net.Http.Headers;
using WinnersPortal.Services.Common;

namespace WinnersPortal.Api.Common;

/// <summary>
/// The one place a service's <see cref="Outcome"/> becomes an HTTP answer.
/// Each kind maps to the status the endpoints answered with before the
/// services existed, so the wire did not move.
/// </summary>
public static class OutcomeResults
{
    public static IResult ToResult(this Outcome outcome) => outcome.Kind switch
    {
        OutcomeKind.Ok => Results.Ok(outcome.Body),
        OutcomeKind.NoContent => Results.NoContent(),
        OutcomeKind.Invalid => Results.BadRequest(outcome.Body),
        OutcomeKind.Unauthorized => Results.Unauthorized(),
        OutcomeKind.Forbidden => Results.Json(outcome.Body, statusCode: StatusCodes.Status403Forbidden),
        OutcomeKind.NotFound => Results.NotFound(outcome.Body),
        OutcomeKind.Conflict => Results.Conflict(outcome.Body),
        OutcomeKind.TooManyRequests => Results.Json(outcome.Body, statusCode: StatusCodes.Status429TooManyRequests),
        OutcomeKind.Unavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        OutcomeKind.Redirect => new WebRedirect(outcome.Location!),
        OutcomeKind.File => Results.Bytes(
            outcome.Content!, outcome.ContentType,
            lastModified: outcome.LastModified,
            entityTag: outcome.ETag is null ? null : new EntityTagHeaderValue(outcome.ETag)),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Kind, "An outcome kind with no HTTP answer."),
    };

    /// <summary>A typed outcome answers exactly as the plain one it carries: the type names the body, it does not change it.</summary>
    public static IResult ToResult<T>(this Outcome<T> outcome) => outcome.Untyped.ToResult();

    /// <summary>
    /// For a link a person opens in the browser (a file download, a file
    /// view): a signed-out click lands on the login page, coming back here
    /// after, instead of a JSON 401.
    /// </summary>
    public static IResult ToBrowserResult(this Outcome outcome, HttpRequest request) =>
        outcome.Kind == OutcomeKind.Unauthorized
            ? new WebRedirect($"/login?next={Uri.EscapeDataString(request.Path)}")
            : outcome.ToResult();
}
