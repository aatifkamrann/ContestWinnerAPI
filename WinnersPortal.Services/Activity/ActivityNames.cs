using WinnersPortal.Services.Common;
using WinnersPortal.Domain;
using System.Text.RegularExpressions;

namespace WinnersPortal.Services.Activity;

/// <summary>
/// The words the activity log uses, and the rules for what gets a row. Pure,
/// so a test can hold every write endpoint to having a name, and so the
/// question "is this request worth a row?" is answered in one place rather
/// than by whichever middleware last touched the pipeline.
///
/// The rule: every request that <em>does</em> something is worth a row —
/// every non-GET under <c>/api</c>, plus the handful of GETs that hand a
/// file over or connect an account. Reads are not: a page that makes six
/// API calls is one visit, and the visit endpoint records that one.
/// </summary>
public static partial class ActivityNames
{
    public const int MaxPath = ActivityEvent.MaxPath;
    public const int MaxSubject = ActivityEvent.MaxSubject;
    public const int MaxAgent = ActivityEvent.MaxAgent;

    /// <summary>A reason's longest, which is a removal reason's longest.</summary>
    public const int MaxDetail = ActivityEvent.MaxDetail;

    /// <summary>
    /// A reason somebody gave, made safe to store on a row: trimmed, blank as
    /// none, and no longer than a removal reason may be.
    /// </summary>
    public static string? Detail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        return s.Length > MaxDetail ? s[..MaxDetail].TrimEnd() : s;
    }

    /// <summary>
    /// A route pattern with its constraints stripped and its trailing slash
    /// dropped: <c>/api/admin/users/{id:guid}/</c> becomes
    /// <c>/api/admin/users/{id}</c>, which is the spelling the names are
    /// keyed by.
    /// </summary>
    public static string Normalize(string pattern)
    {
        var s = Constraint().Replace(pattern, "{$1}");
        if (!s.StartsWith('/')) s = "/" + s;
        return s.Length > 1 ? s.TrimEnd('/') : s;
    }

    [GeneratedRegex(@"\{([^}:]+):[^}]*\}")]
    private static partial Regex Constraint();

    /// <summary>Whether a completed request earns a row.</summary>
    public static bool Worth(string method, string pattern)
    {
        // Machines, not people: GitHub's deliveries, the live hub, the
        // health probe — and the visit endpoint, which records itself.
        if (pattern.StartsWith("/api/webhooks", StringComparison.Ordinal)
            || pattern.StartsWith("/api/activity", StringComparison.Ordinal)
            || pattern.StartsWith("/api/live", StringComparison.Ordinal)
            || pattern.StartsWith("/api/health", StringComparison.Ordinal))
            return false;
        if (Requests.IsRead(method))
            return Names.ContainsKey($"GET {pattern}");
        return true;
    }

    /// <summary>What a request did, in words; the method and pattern when nothing better is known.</summary>
    public static string Describe(string method, string pattern) =>
        Names.TryGetValue($"{method.ToUpperInvariant()} {pattern}", out var name) ? name : $"{method.ToUpperInvariant()} {pattern}";

    public static bool Described(string method, string pattern) =>
        Names.ContainsKey($"{method.ToUpperInvariant()} {pattern}");

    /// <summary>
    /// Every request the log names. Keyed by method and normalized pattern;
    /// the value is what an administrator reads. Written from the person's
    /// side — "Applied to an opportunity", not "POST applications".
    /// </summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        // the door
        ["POST /api/auth/register"] = "Created an account",
        ["POST /api/auth/confirm"] = "Entered the confirmation code",
        ["POST /api/auth/resend-code"] = "Asked for a new confirmation code",
        ["POST /api/auth/login"] = "Signed in",
        ["POST /api/auth/logout"] = "Signed out",
        ["POST /api/auth/token"] = "Signed in for a bearer token",
        ["POST /api/auth/token/refresh"] = "Renewed a bearer token",
        ["POST /api/auth/token/revoke"] = "Revoked a bearer token",
        ["POST /api/auth/forgot-password"] = "Asked for a password reset link",
        ["POST /api/auth/reset-password"] = "Reset the password from a link",
        ["POST /api/auth/change-password"] = "Changed their password",
        ["PUT /api/auth/theme"] = "Changed the theme colours",
        ["POST /api/auth/accept-terms"] = "Accepted the terms of service",
        ["POST /api/setup"] = "Completed the setup wizard",
        ["POST /api/setup/database/test"] = "Tested a database during setup",

        // opportunities, as a client
        // The handler names these itself (OpportunitySaved below): a save on
        // the way to publishing says so, and the subject is the slug.
        ["POST /api/opportunities"] = "Saved a new opportunity draft",
        ["PUT /api/opportunities/{id}"] = "Edited an opportunity draft",
        ["POST /api/opportunities/{id}/publish"] = "Published an opportunity",
        ["POST /api/opportunities/{id}/cancel"] = "Cancelled an opportunity",
        ["POST /api/opportunities/{id}/award"] = "Announced a winner",
        ["POST /api/awards/{id}/paid"] = "Marked an award paid",
        ["PUT /api/awards/{id}/rating"] = "Rated the other party",
        ["POST /api/opportunities/{id}/attachments"] = "Started attaching a file to a brief",
        ["PUT /api/attachments/{id}/content"] = "Attached a file to a brief",
        ["DELETE /api/attachments/{id}"] = "Removed a file from a brief",
        ["GET /api/attachments/{id}/download"] = "Downloaded a brief attachment",
        ["GET /api/attachments/{id}/view"] = "Viewed a brief attachment",
        ["GET /api/attachments/{id}/text"] = "Viewed a brief attachment",
        // The direction rides in the body, which no row reads; the handler
        // names its own row (Decisions below) and this is what a malformed
        // request reads as.
        ["POST /api/applications/{id}/decide"] = "Decided an application",
        ["POST /api/entries/{id}/remove"] = "Removed an entrant",
        ["POST /api/entries/{id}/zip"] = "Requested an entry's ZIP",

        // opportunities, as a freelancer
        ["POST /api/opportunities/{slug}/applications"] = "Applied to an opportunity",
        ["POST /api/entries/{id}/withdraw"] = "Withdrew from an opportunity",
        ["POST /api/entries/{id}/submissions"] = "Started handing in a file",
        ["PUT /api/submissions/{id}/content"] = "Handed in a file",
        ["DELETE /api/submissions/{id}"] = "Removed a handed-in file",
        ["GET /api/submissions/{id}/download"] = "Downloaded a handed-in file",
        ["GET /api/checkpoints/{id}/build-log"] = "Downloaded a milestone's build log",
        ["POST /api/previews/{id}"] = "Started a preview",
        ["DELETE /api/previews/{id}"] = "Stopped a preview",
        ["GET /api/previews/{id}/open"] = "Opened a preview",
        ["GET /api/submissions/{id}/view"] = "Viewed a handed-in file",
        ["GET /api/submissions/{id}/text"] = "Viewed a handed-in file",

        // the account
        ["PUT /api/profile"] = "Saved the profile",
        ["PUT /api/notifications"] = "Changed notification preferences",
        ["PUT /api/notifications/inbox/{id}"] = "Marked a notification read or unread",
        ["PUT /api/notifications/inbox"] = "Marked the whole inbox read or unread",
        ["POST /api/notifications/devices"] = "Turned on push for a browser",
        ["DELETE /api/notifications/devices/{id}"] = "Turned off push for a browser",
        ["POST /api/notifications/unsubscribe"] = "Unsubscribed from an email",
        ["GET /api/github/connect"] = "Started connecting a GitHub account",
        ["GET /api/github/callback"] = "Connected a GitHub account",

        // AI drafts asked for
        ["POST /api/opportunities/ai/{feature}"] = "Asked AI for a draft on the opportunity form",
        ["POST /api/opportunities/{slug}/ai/approach"] = "Asked AI for an approach draft",
        ["POST /api/profile/ai/summary"] = "Asked AI for a profile summary",
        ["POST /api/opportunities/{id}/ai/{feature}"] = "Asked AI to read an opportunity",
        ["POST /api/opportunities/{id}/ai/standing"] = "Asked AI for standing notes",
        ["POST /api/entries/{id}/ai/digest"] = "Asked AI for an entry digest",

        // administration
        ["PUT /api/settings"] = "Changed settings",
        ["POST /api/settings/logo"] = "Uploaded the logo",
        ["DELETE /api/settings/logo"] = "Removed the logo",
        ["POST /api/settings/icon"] = "Uploaded the tab icon",
        ["DELETE /api/settings/icon"] = "Removed the tab icon",
        ["POST /api/settings/test-email"] = "Tested the email settings",
        ["POST /api/settings/test-phone"] = "Tested the phone settings",
        ["POST /api/settings/test-github"] = "Tested the GitHub settings",
        ["POST /api/settings/test-storage"] = "Tested the storage settings",
        ["POST /api/settings/test-ai"] = "Tested the AI settings",
        ["POST /api/settings/test-identity"] = "Tested the identity verification settings",
        ["POST /api/settings/test-preview"] = "Tested the build host settings",
        ["POST /api/admin/users/{id}/reset-verification"] = "Took back an account's identity verification",
        ["POST /api/admin/users/{id}/identity/fetch"] = "Fetched a verification's proof again",
        ["GET /api/admin/identity-documents/{id}/view"] = "Viewed an identity document",
        ["POST /api/identity/session"] = "Started verifying their identity",
        ["POST /api/identity/refresh"] = "Asked for their verification verdict",
        ["POST /api/settings/revoke-sessions"] = "Revoked every session",
        ["POST /api/admin/users"] = "Created an account for somebody",
        ["PUT /api/admin/users/{id}"] = "Corrected an account's details",
        ["POST /api/admin/users/{id}/lock"] = "Locked an account",
        ["POST /api/admin/users/{id}/unlock"] = "Unlocked an account",
        ["POST /api/admin/users/{id}/password"] = "Set an account's password",
        ["POST /api/admin/users/{id}/reset-link"] = "Sent an account a reset link",
        ["DELETE /api/admin/users/{id}"] = "Deleted an account",
        ["POST /api/admin/entries/{id}/retry-provision"] = "Retried a repository's provisioning",
        ["POST /api/admin/awards/{id}/restart-handover"] = "Restarted a handover",
        ["POST /api/admin/deliveries/{id}/replay"] = "Replayed a webhook delivery",
        ["POST /api/admin/slow-queries/clear"] = "Cleared the slow-query tally",
        ["POST /api/admin/database/test"] = "Tested a database to move to",
        ["POST /api/admin/database/move"] = "Started moving the portal to another database",
    };

    /// <summary>The names, for the test that holds every write endpoint to having one.</summary>
    public static IReadOnlyCollection<string> Named => Names.Keys;

    /// <summary>
    /// A decision on an application, in words, for the handler to set as
    /// <see cref="ActivityNote.Action"/>: the middleware cannot tell a
    /// selection from a pass, and a pass that undoes an earlier selection
    /// is a third thing.
    /// </summary>
    public static string Decision(bool selected, bool takenBack) =>
        selected ? "Selected an applicant" : takenBack ? "Took a selection back" : "Passed on an applicant";

    /// <summary>
    /// An opportunity save, in words. A save only ever writes a draft — publishing
    /// is its own request and its own row — but the editor's Publish button
    /// saves first, and that save says it was on the way to publishing, so
    /// an administrator reading two rows a second apart knows they were one
    /// click.
    /// </summary>
    public static string OpportunitySaved(bool isNew, bool publishing) =>
        (isNew, publishing) switch
        {
            (true, true) => "Saved a new opportunity to publish it",
            (true, false) => "Saved a new opportunity draft",
            (false, true) => "Saved an opportunity draft to publish it",
            (false, false) => "Edited an opportunity draft",
        };

    // ------------------------------------------------------------ pages

    /// <summary>
    /// A page path as the browser reports it, made safe to store: a path on
    /// this portal, no query string or fragment (a reset link's token rides
    /// in the query, and must never land in a log), no scheme or host, and
    /// short. Null when it is not a path at all.
    /// </summary>
    public static string? CleanPage(string? page)
    {
        if (string.IsNullOrWhiteSpace(page)) return null;
        var s = page.Trim();
        var cut = s.IndexOfAny(['?', '#']);
        if (cut >= 0) s = s[..cut];
        if (!s.StartsWith('/') || s.StartsWith("//", StringComparison.Ordinal)) return null;
        if (s.Length > 1) s = s.TrimEnd('/');
        return s.Length > MaxPath ? s[..MaxPath] : s;
    }

    /// <summary>The page behind a Referer header — its path, by the same rule — or null.</summary>
    public static string? PageOf(string? referer)
    {
        if (string.IsNullOrWhiteSpace(referer)) return null;
        if (Uri.TryCreate(referer, UriKind.Absolute, out var uri)) return CleanPage(uri.AbsolutePath);
        return CleanPage(referer);
    }

    /// <summary>
    /// What a page is called, from its path — the menu's word for it where
    /// there is one, so a visit reads "Opened the dashboard" rather than a
    /// path an administrator has to decode.
    /// </summary>
    public static string PageName(string page)
    {
        var parts = page.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "Opened the landing page",
            _ => parts[0] switch
            {
                "opportunities" when parts.Length >= 3 && parts[2] == "apply" => "Opened an opportunity's application form",
                "opportunities" when parts.Length >= 2 => "Opened an opportunity",
                "opportunities" => "Opened the opportunities feed",
                "client" when parts.Length >= 3 && parts[2] == "new" => "Opened the new opportunity form",
                "client" when parts.Length >= 4 && parts[3] == "edit" => "Opened an opportunity's edit form",
                "client" => "Opened My opportunities",
                "entries" => "Opened My Opportunities",
                "dashboard" => "Opened the dashboard",
                "profile" when parts.Length >= 2 => "Opened somebody's profile",
                "profile" => "Opened their profile",
                "welcome" => "Opened the welcome page",
                "notifications" when parts.Length >= 2 => "Opened the unsubscribe page",
                "notifications" => "Opened notification settings",
                "login" => "Opened the sign-in page",
                "register" => "Opened the join page",
                "forgot-password" => "Opened the forgot-password page",
                "reset-password" => "Opened the reset-password page",
                "setup" => "Opened the setup wizard",
                "terms" => "Opened the terms of service",
                "privacy" => "Opened the privacy policy",
                "admin" when parts.Length >= 2 => $"Opened Admin → {Capitalize(parts[1])}",
                "reports" when parts.Length >= 2 => $"Opened the {parts[1]} report",
                _ => $"Opened {page}",
            },
        };
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ---------------------------------------------------------- subject

    /// <summary>
    /// What the request was about, read off the route: the slug or id in
    /// the path, never anything from the body. Several are joined; none is
    /// long.
    /// </summary>
    public static string? Subject(IEnumerable<KeyValuePair<string, object?>> routeValues)
    {
        var parts = routeValues
            .Select(kv => kv.Value?.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
        if (parts.Count == 0) return null;
        var s = string.Join(" · ", parts);
        return s.Length > MaxSubject ? s[..MaxSubject] : s;
    }

    public static string? Trim(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length > max ? s[..max] : s;

    /// <summary>A browser session id as the visitor cookie carries it: sixteen lowercase hex digits.</summary>
    public static bool IsVisitorId(string? v) =>
        v is { Length: 16 } && v.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
