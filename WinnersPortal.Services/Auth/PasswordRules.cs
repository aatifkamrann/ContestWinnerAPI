using System.Diagnostics.CodeAnalysis;

namespace WinnersPortal.Services.Auth;

/// <summary>
/// The one password rule, shared by registration, the reset flow, the setup
/// wizard's first administrator and the administrator's account screen — so
/// a reset can never set a password that registration would have refused.
///
/// Five requirements rather than one length. A length on its own passes
/// "aaaaaaaaaa", and nobody picking a password is thinking about entropy —
/// they are thinking about what the form will accept. So the form lists
/// these as you type and ticks them off one by one, and the list it shows
/// (web/src/lib/password.ts) is this list. This one decides.
/// </summary>
public static class PasswordRules
{
    public const int MinLength = 8;

    /// <summary>What a password must have, in the order the join form lists them.</summary>
    public static readonly IReadOnlyList<(string Label, Func<string, bool> Met)> Requirements =
    [
        ("8 or more characters", p => p.Length >= MinLength),
        ("An uppercase letter", p => p.Any(char.IsUpper)),
        ("A lowercase letter", p => p.Any(char.IsLower)),
        ("A number", p => p.Any(char.IsDigit)),
        ("A special character", p => p.Any(c => !char.IsLetterOrDigit(c))),
    ];

    /// <summary>
    /// One sentence naming every requirement, for the API's answer. The form
    /// shows the checklist instead; this is what a caller that is not the
    /// form gets, and what somebody sees who had scripting turned off.
    /// </summary>
    public static readonly string Error =
        $"Password must be at least {MinLength} characters and include an uppercase letter, "
        + "a lowercase letter, a number and a special character.";

    public static bool Acceptable([NotNullWhen(true)] string? password) =>
        password is not null && Requirements.All(r => r.Met(password));

    /// <summary>Which requirements this password has not met yet, in list order.</summary>
    public static IReadOnlyList<string> Unmet(string? password) =>
        Requirements.Where(r => !r.Met(password ?? "")).Select(r => r.Label).ToList();
}
