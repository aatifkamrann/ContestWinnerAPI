using WinnersPortal.Domain;

namespace WinnersPortal.Services.Live;

/// <summary>
/// The naming and gating rules of the live board, kept pure so the tests can
/// hold them still. The design rule everything here serves: <b>the hub
/// carries no data</b> — a broadcast says only "this opportunity changed", and
/// the page refetches through the same server render it already trusts.
/// One source of truth; the socket is just a doorbell.
/// </summary>
public static class LiveRules
{
    /// <summary>The one client-side event name. Changing it strands every open tab.</summary>
    public const string OpportunityChanged = "opportunityChanged";

    /// <summary>
    /// SignalR group per opportunity, keyed by slug because that is what the
    /// public page knows. Normalised, so a differently-cased slug in the URL
    /// still lands in the same room.
    /// </summary>
    public static string OpportunityGroup(string slug) => "opportunity:" + slug.Trim().ToLowerInvariant();

    /// <summary>
    /// Drafts are visible only to their author; letting anyone hold a socket
    /// on a guessed draft slug would leak that it exists and when it is
    /// edited. Everything published — including cancelled and awarded pages,
    /// which still change (ratings, payment) — is watchable.
    /// </summary>
    public static bool CanWatch(OpportunityStatus status) => status != OpportunityStatus.Draft;
}
