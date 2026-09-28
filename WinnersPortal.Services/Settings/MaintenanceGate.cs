using WinnersPortal.Services.Common;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// limits.maintenanceMode, enforced in one place. When the switch is on the
/// API answers 503 for everyone except administrators — who need the portal
/// up to verify a deployment and to turn the switch back off — and except
/// the paths that must survive an outage: health checks, the login door the
/// admin walks through, the public branding the shell renders, and above
/// all the webhook receiver, because GitHub keeps delivering during
/// maintenance and a dropped delivery is a milestone claim lost.
/// </summary>
public static class MaintenanceGate
{
    /// <summary>
    /// Paths that stay up for everybody. Register is deliberately absent —
    /// new accounts can wait out an outage; a signed-in admin cannot.
    /// </summary>
    private static readonly string[] OpenPrefixes =
    [
        "/api/health",
        "/api/public",     // branding + the maintenance flag itself
        "/api/auth/login",
        "/api/auth/token",  // the same door, for a client that holds no cookie
        "/api/auth/logout",
        "/api/auth/me",
        // The admin who forgot their password mid-outage is the whole
        // reason the login door is open; a reset is the same door.
        "/api/auth/forgot-password",
        "/api/auth/reset-password",
        "/api/setup",      // a half-installed portal still finishes installing
        "/api/settings",   // admin-gated already; this is where the switch is
        "/api/help",       // the settings screen explains itself even now
        "/api/webhooks",   // GitHub does not pause for maintenance
    ];

    /// <summary>Pure so the rule is testable: does this request pass while maintenance is on?</summary>
    public static bool Allows(string path, bool isAdmin)
    {
        if (isAdmin) return true;
        if (!Requests.StartsWithSegments(path, "/api")) return true; // static assets, Next pages
        foreach (var prefix in OpenPrefixes)
            if (Requests.StartsWithSegments(path, prefix)) return true;
        return false;
    }

    /// <summary>
    /// Paths that stay up while a database move has the portal paused —
    /// a narrower door than maintenance, and one administrators do not
    /// pass either: reads, which touch nothing the copy could miss; the
    /// page and the wizard driving the move; the health probe; and
    /// signing out. Everything that writes waits for the restart, the
    /// GitHub receiver included — a delivery refused now is replayable
    /// from Operations, a claim written to the old database is not.
    /// </summary>
    private static readonly string[] PausedPrefixes =
    [
        "/api/admin/database",
        "/api/setup",
        "/api/health",
        "/api/auth/logout",
    ];

    /// <summary>Pure so the rule is testable: does this request pass while the portal is paused for a move?</summary>
    public static bool AllowsWhilePaused(string path, string method)
    {
        if (!Requests.StartsWithSegments(path, "/api")) return true;
        if (Requests.IsRead(method)) return true;
        foreach (var prefix in PausedPrefixes)
            if (Requests.StartsWithSegments(path, prefix)) return true;
        return false;
    }
}
