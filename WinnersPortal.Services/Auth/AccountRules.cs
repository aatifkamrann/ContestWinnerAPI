using WinnersPortal.Domain;

namespace WinnersPortal.Services.Auth;

/// <summary>
/// What an administrator may do to an account, as pure rules — so the two
/// guards that matter (never lock yourself out, never leave the portal
/// without an administrator who can sign in) are tested, not remembered.
/// </summary>
public static class AccountRules
{
    public const int MaxLockReason = User.MaxLockReason;

    /// <summary>
    /// Shown at the login door. Says nothing about why: the reason is the
    /// administrator's note, and a person who was locked out for cause is
    /// told by the administrator, not by an error message.
    /// </summary>
    public const string LockedMessage = "This account is locked. Contact the portal administrator.";

    /// <summary>What an erased account is called wherever its records still show.</summary>
    public const string ErasedName = "Deleted member";

    public const int MinDisplayName = 2;
    public const int MaxDisplayName = 80;

    /// <summary>
    /// A display name as it will be stored, and what is wrong with it if
    /// anything is. Runs of whitespace collapse to one space and control
    /// characters go: this name is rendered in a row beside other people's,
    /// and a pasted newline should not be able to stretch it. The erased
    /// placeholder is refused outright — it is the one name on this portal
    /// that has to keep meaning what it says. Everything else is allowed:
    /// people's names are not a character class, and a portal that argues
    /// with somebody about how theirs is spelt has already lost.
    /// </summary>
    public static (string Name, string? Problem) CleanDisplayName(string? raw)
    {
        var printable = new string((raw ?? "")
            .Where(c => !char.IsControl(c) || char.IsWhiteSpace(c))
            .ToArray());
        var name = string.Join(' ', printable.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (name.Length is < MinDisplayName or > MaxDisplayName)
            return (name, $"Display name must be {MinDisplayName}–{MaxDisplayName} characters.");
        if (string.Equals(name, ErasedName, StringComparison.OrdinalIgnoreCase))
            return (name, $"“{ErasedName}” is what this portal calls a closed account. Pick another name.");
        return (name, null);
    }

    /// <summary>
    /// The address an erased account keeps: unique (the id is in it), valid
    /// to the column, and on a reserved domain nothing can ever deliver to.
    /// </summary>
    public static string ErasedEmail(Guid id) => $"erased-{id:N}@erased.invalid";

    /// <summary>Whether the account can hold a session at all.</summary>
    public static bool CanSignIn(User user) => user.LockedAtUtc is null && user.ErasedAtUtc is null;

    /// <summary>
    /// The roles an administrator may create. Registration offers the two
    /// public ones; this list carries the third, because after the setup
    /// wizard has run there is nowhere else a second administrator can come
    /// from — and a portal with exactly one is a portal one forgotten
    /// password away from having none.
    /// </summary>
    public static readonly IReadOnlyList<string> CreatableRoles =
        [Roles.Freelancer, Roles.Client, Roles.Admin];

    /// <summary>Null when the role may be created, else what to say instead.</summary>
    public static string? RoleProblem(string? role) =>
        role is not null && CreatableRoles.Contains(role)
            ? null
            : "Choose an account type: freelancer, client or administrator.";

    /// <summary>Trimmed, or null for none. Too long is refused, not cut: a note is the administrator's words.</summary>
    public static (bool Ok, string? Reason) CleanLockReason(string? reason)
    {
        var r = reason?.Trim();
        if (string.IsNullOrEmpty(r)) return (true, null);
        return r.Length <= MaxLockReason ? (true, r) : (false, null);
    }

    /// <summary>
    /// Null when the account may be locked or deleted, else why not. The
    /// guards are the same for both: the acting administrator's own account,
    /// and the last administrator who can still sign in.
    /// </summary>
    public static string? RemovalProblem(string action, bool targetIsSelf, bool targetIsAdmin, int otherActiveAdmins)
    {
        if (targetIsSelf)
            return $"You cannot {action} your own account — ask another administrator.";
        if (targetIsAdmin && otherActiveAdmins == 0)
            return "This is the only administrator account that can sign in. The portal has to keep one.";
        return null;
    }

    /// <summary>
    /// Whether deleting removes the row or erases it in place. Opportunities,
    /// entries and ratings are other people's records too — a paid award
    /// with no winner on it is a dispute nobody can settle — so an account
    /// they point at keeps its row and loses its person.
    /// </summary>
    public static bool LeavesNoTrace(int opportunities, int entries, int ratings) =>
        opportunities == 0 && entries == 0 && ratings == 0;
}
