namespace WinnersPortal.Services.Ai;

/// <summary>
/// The name an AI draft carries on a member's screen ("AI draft · YupAI ·
/// just now"). Which vendor and model wrote it stays in the artifact row,
/// the log and the activity log's external calls, where an administrator
/// reads it; no answer a member's browser receives names either.
/// </summary>
public static class AiBrand
{
    public const string Name = "YupAI";

    /// <summary>
    /// The provider as a member may see it: null and "local" (work that
    /// never left the server) pass through, every model provider is
    /// <see cref="Name"/>.
    /// </summary>
    public static string? Public(string? provider) => provider is null or "local" ? provider : Name;
}
