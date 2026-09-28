namespace WinnersPortal.Domain;

public static class ActivityKinds
{
    /// <summary>A page opened in the browser, signed in or not.</summary>
    public const string Visit = "visit";

    /// <summary>A request that did something: a write, a download, a sign-in.</summary>
    public const string Action = "action";

    /// <summary>
    /// A call the portal made to somebody else's API — a model, the identity
    /// provider, GitHub, the SMS gateway — with what was sent and what came
    /// back, secrets masked.
    /// </summary>
    public const string External = "external";
}

/// <summary>
/// One thing somebody did on the portal, for an administrator to read back
/// later: who, when, what, and on which page. Three kinds — a page opened,
/// which the browser reports as it navigates; an action, which the API
/// records itself as the request completes; and a call the portal made to
/// a third-party API, recorded by the HTTP client that made it.
///
/// What is deliberately <b>not</b> here: request bodies, query strings, and
/// anything typed into a form — save the reason a handler hands over on
/// purpose for a withdrawal or a removal (<see cref="Detail"/>), which is an
/// account of a decision rather than content. A row says that a password was changed,
/// never what it was changed to; that an opportunity was edited, never the brief.
/// The row is evidence that something happened, not a copy of it.
///
/// The one exception is the third-party call (<see cref="ActivityKinds.External"/>):
/// what the portal sent to another company and what it answered is the
/// point of that row, so it keeps both (<see cref="Request"/>,
/// <see cref="Response"/>) — with keys, tokens, codes and a verification's
/// extracted identity masked before it is written.
///
/// No foreign key to <see cref="User"/>: an account removed outright takes
/// nothing with it, and the rows it leaves read as a removed account. A
/// visitor who never signed in has no user at all and is told apart from
/// the next one by the visitor id, a session cookie the visit endpoint
/// issues, which dies with the browser — it is not a tracker.
/// </summary>
public sealed class ActivityEvent
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxPath = 300;
    public const int MaxSubject = 200;
    public const int MaxAgent = 300;
    public const int MaxDetail = 500;
    public const int MaxService = 16;

    public long Id { get; set; }

    /// <summary>The signed-in account, or null for a visitor.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The browser session, 16 hex characters; null when the request carried no cookie.</summary>
    public string? Visitor { get; set; }

    /// <summary>One of <see cref="ActivityKinds"/>.</summary>
    public required string Kind { get; set; }

    public required string Method { get; set; }

    /// <summary>The API path for an action; the page path for a visit. Never a query string.</summary>
    public required string Path { get; set; }

    /// <summary>What happened, in words: "Applied to an opportunity", "Opened the dashboard".</summary>
    public required string Action { get; set; }

    /// <summary>The page the browser was on, from the Referer; for a visit, the page itself.</summary>
    public string? Page { get; set; }

    /// <summary>What it was done to, from the route: an opportunity slug, an id.</summary>
    public string? Subject { get; set; }

    /// <summary>
    /// The reason given, where the action was a withdrawal or a removal — the
    /// one typed thing a row holds, and only when the handler passes it on
    /// purpose. Up to 500 characters; cleared when the account is erased.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>The HTTP status the request ended with; 0 for a visit.</summary>
    public int Status { get; set; }

    public string? Ip { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    /// <summary>
    /// For a third-party call, which kind of service answered: ai, identity,
    /// github, sms, captcha, push, storage. Null on every other row.
    /// </summary>
    public string? Service { get; set; }

    /// <summary>For a third-party call, how long the answer took, in milliseconds.</summary>
    public int? DurationMs { get; set; }

    /// <summary>
    /// For a third-party call, what was sent: the request line, headers and
    /// body, secrets masked. Cleared when the account is erased.
    /// </summary>
    public string? Request { get; set; }

    /// <summary>
    /// For a third-party call, what came back: the status, headers and body,
    /// or why nothing did. Cleared when the account is erased.
    /// </summary>
    public string? Response { get; set; }
}
