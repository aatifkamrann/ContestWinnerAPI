namespace WinnersPortal.Services.Ai;

/// <summary>
/// The name an AI draft carries on a member's screen: the portal's own
/// (Branding → Portal name), so "AI draft · Winners Portal · just now" on a
/// portal left at the default. Which vendor and model wrote it stays in the
/// artifact row, the log and the activity log's external calls, where an
/// administrator reads it; no answer a member's browser receives names
/// either. The name is read per answer (<see cref="Settings.AiOptions.PublicNameAsync"/>),
/// so a rename shows on the next draft without a restart.
/// </summary>
public static class AiBrand
{
    /// <summary>The name when Branding has none — the setting's own default.</summary>
    public const string DefaultName = "Winners Portal";

    /// <summary>
    /// The provider as a member may see it: null and "local" (work that
    /// never left the server) pass through, every model provider is the
    /// portal's name.
    /// </summary>
    public static string? Public(string? provider, string portalName) =>
        provider is null or "local" ? provider : portalName;

    /// <summary>The stored portal name, or <see cref="DefaultName"/> when it is blank.</summary>
    public static string NameOf(string? portalName) =>
        string.IsNullOrWhiteSpace(portalName) ? DefaultName : portalName.Trim();
}
