namespace WinnersPortal.Domain;

/// <summary>
/// One day's count of provider calls, for the portal as a whole or for one
/// member: the daily ceiling and the per-member cap are enforced against
/// these rows, each counted in a single statement so two calls arriving
/// together cannot both read the same number and both pass. The portal's
/// own row carries <see cref="Guid.Empty"/> as its member, so there is
/// exactly one of it per day on either database.
/// </summary>
public sealed class AiUsage
{
    /// <summary>The UTC calendar day, "2026-10-01".</summary>
    public string Day { get; set; } = "";

    /// <summary>The member who spent, or <see cref="Guid.Empty"/> for the portal's own count.</summary>
    public Guid UserId { get; set; }

    /// <summary>Calls counted today; a call the provider never ran is given back.</summary>
    public int Calls { get; set; }

    /// <summary>Tokens sent today, as the providers counted them; a daily budget is measured against these at today's prices.</summary>
    public long InputTokens { get; set; }

    /// <summary>Tokens answered today, reasoning included.</summary>
    public long OutputTokens { get; set; }
}
