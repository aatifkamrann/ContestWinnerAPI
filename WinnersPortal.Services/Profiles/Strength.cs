namespace WinnersPortal.Services.Profiles;

/// <summary>
/// Profile strength: the seven steps of joining, each done or not, and the
/// share of them that is. It is the join rail's list read back off the
/// saved profile — the same steps, under the same names, in the same
/// order (the rail is components/SignupRail.tsx on the web; a rename
/// there is a rename here), with a tick where the section has what it
/// asked for — and it is deliberately not the merit score. The score is what a client leans on; this is the member's
/// own checklist, and a checklist rounds to a whole number of ticks.
///
/// "Done" is the least the section is for, not the most it can hold: one
/// skill ticks Expertise, one project ticks Portfolio. A bar that only
/// filled at twenty projects would be a bar nobody reached the end of.
/// </summary>
public static class Strength
{
    /// <summary>Everything a tick is decided on.</summary>
    public sealed record Facts(
        bool Confirmed,
        bool HasHeadline,
        bool HasBio,
        bool HasCategory,
        int Skills,
        int Projects,
        bool HasAvailability,
        int Payments);

    /// <summary>One line of the box: the step's name, whether it is done, and what done means.</summary>
    public sealed record Step(string Label, bool Done, string Detail);

    public static IReadOnlyList<Step> Steps(Facts f) =>
    [
        // Somebody reading their own strength has an account: the first
        // step is done by definition, and the box says so rather than
        // starting a seven-step list at two.
        new("Account", true, "Your name, how to reach you, and a password."),
        new("Confirm it's you", f.Confirmed, "An email address or phone number you proved is yours."),
        new("Professional identity", f.HasHeadline && f.HasBio, "A professional title and an About You."),
        new("Expertise", f.HasCategory && f.Skills > 0, "A kind of work you do, and at least one skill."),
        new("Portfolio", f.Projects > 0, "At least one project you have built."),
        new("Availability", f.HasAvailability, "Whether you can start, and when."),
        new("Payout Setup", f.Payments > 0, "A way for a client to pay you."),
    ];

    /// <summary>Ticks over steps, as a whole percentage: six of seven is 86.</summary>
    public static int Percent(Facts f)
    {
        var steps = Steps(f);
        return (int)Math.Round(100.0 * steps.Count(s => s.Done) / steps.Count);
    }
}
