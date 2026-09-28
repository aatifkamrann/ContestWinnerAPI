using WinnersPortal.Services.Ai;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// Taking an entrant off an opportunity: who may, when, and what they are owed.
///
/// This is the harshest thing a client can do to one person on this portal —
/// somebody staked days of work on an open-entry promise and is being told it
/// does not count. So the rules are deliberately narrow: never the winner,
/// never on an opportunity that is already decided, never without a reason the
/// entrant receives word for word, and never without a window to clone what
/// they built. Everything here is pure, so all of that is testable.
/// </summary>
public static class Removal
{
    /// <summary>Short enough to be no reason at all; the entrant deserves a sentence.</summary>
    public const int MinReason = 10;

    public const int MaxReason = Entry.MaxReason;

    /// <summary>
    /// How long a removed entrant keeps read access to their own repository.
    /// Removal archives the repo immediately, so nothing can be pushed into
    /// it — this window is only for getting the work out, and the email says
    /// so on the day it starts.
    /// </summary>
    public const int CloneGraceDays = 7;

    public static (bool Ok, string? Reason) CleanReason(string? reason)
    {
        var trimmed = reason?.Trim() ?? "";
        if (trimmed.Length < MinReason)
            return (false, null);
        return (true, trimmed.Length > MaxReason ? trimmed[..MaxReason] : trimmed);
    }

    /// <summary>
    /// Why this removal cannot happen, or null if it can. Ordered so the
    /// answer names the real obstacle rather than the first one checked.
    /// </summary>
    public static string? Problem(
        OpportunityStatus opportunityStatus, EntryStatus entryStatus, bool isWinner, bool byAdmin)
    {
        if (isWinner)
            return "This entry won the opportunity. An announced award is a promise on the public "
                + "record — it cannot be undone by removing the winner.";

        if (entryStatus == EntryStatus.Withdrawn)
            return "This entrant already withdrew.";
        if (entryStatus == EntryStatus.Removed)
            return "This entrant has already been removed.";
        if (entryStatus == EntryStatus.Deselected)
            return "This entrant’s selection was already taken back.";

        return opportunityStatus switch
        {
            OpportunityStatus.Open or OpportunityStatus.Reviewing => null,
            OpportunityStatus.Draft =>
                "A draft has no entrants yet.",
            OpportunityStatus.Cancelled =>
                "This opportunity was cancelled; every entry is already void.",
            _ => byAdmin
                ? "This opportunity has been decided. Its entrant list is now a record of what happened."
                : "This opportunity has been decided — its entrant list is the record of what happened, "
                    + "and removing somebody from it now would rewrite that record.",
        };
    }

    /// <summary>The end of the clone window, from the moment of removal.</summary>
    public static DateTimeOffset AccessEndsAt(DateTimeOffset removedAtUtc) =>
        removedAtUtc.AddDays(CloneGraceDays);

    /// <summary>
    /// Whether the worker should take the collaborator off the repository
    /// now. False while the window is open, and false once it already has —
    /// the sweep runs every cycle and must not keep calling GitHub.
    /// </summary>
    public static bool AccessDue(
        DateTimeOffset? accessEndsAtUtc, DateTimeOffset? alreadyRevokedAtUtc, DateTimeOffset now) =>
        accessEndsAtUtc is not null && alreadyRevokedAtUtc is null && accessEndsAtUtc <= now;

    /// <summary>
    /// A removed entrant does not get to walk back in. The database's partial
    /// unique index only guards active rows, so this is the check that makes
    /// removal mean anything at all.
    /// </summary>
    public static string? ReentryProblem(bool wasRemoved) =>
        wasRemoved
            ? "You were removed from this opportunity by the client, so you cannot enter it again. "
                + "Their reason is on your entries page."
            : null;
}
