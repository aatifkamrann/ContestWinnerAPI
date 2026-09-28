namespace WinnersPortal.Services.Activity;

/// <summary>
/// What the code serving a request knows about it that the route does not,
/// for the activity log's row: who the person is before the session says so
/// (a sign-in, a registration, a reset link), the words for what happened
/// when the verb and path cannot tell a selection from a take-back, what it
/// was about, and the one typed detail a row may hold. Scoped to the request;
/// a service writes it, and the activity middleware reads it once the
/// response is written.
/// </summary>
public sealed class ActivityNote
{
    /// <summary>
    /// Who the person is, when the service knows before the session does —
    /// sign-in, registration, a reset link. Read ahead of the principal.
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// The words for what happened, when they depend on something the
    /// middleware must not read — the body. A decision on an application is
    /// "Selected an applicant" or "Passed on an applicant"; the route alone
    /// only knows it was decided. Preferred to the name keyed by the route.
    /// </summary>
    public string? Action { get; set; }

    /// <summary>
    /// What the request was about, when the service knows better than the
    /// route: a new opportunity has no id in its path, and an opportunity's slug
    /// reads better than its id. Preferred to the route values. A name,
    /// never a body.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// The one typed thing a row may hold: the reason given for a withdrawal
    /// or a removal. Set on purpose by the service that read it, never
    /// gathered from a body by the middleware.
    /// </summary>
    public string? Detail { get; set; }
}
