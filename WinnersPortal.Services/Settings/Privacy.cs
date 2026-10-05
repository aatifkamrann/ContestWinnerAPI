using System.Text.RegularExpressions;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// The privacy policy: what the portal collects, why, and what becomes of
/// it. Its own document rather than a section of the terms, because the two
/// answer different questions — one is what you agree to, the other is what
/// is done with your data — and somebody looking for the second should not
/// have to read the first to find it.
///
/// Unversioned, unlike <see cref="Terms"/>. A terms version exists to make
/// every account accept again; a privacy policy is a statement of practice
/// rather than an agreement, so there is nothing to re-accept and nothing to
/// count. The join form's one checkbox links both documents.
///
/// Empty by default, like the terms: a portal ships without one and the
/// /privacy page says so, rather than shipping boilerplate that would be
/// wrong for whoever deployed it.
/// </summary>
public static class Privacy
{
    public const string MarkdownKey = "legal.privacyMarkdown";

    /// <summary>The portal has a policy once there is text to read.</summary>
    public static bool Exists(string? markdown) => !string.IsNullOrWhiteSpace(markdown);

    /// <summary>
    /// The words a policy that owns up to AI processing would use — any
    /// one of them is taken as the admission. A looser reading than a
    /// lawyer's, on purpose: the check exists to catch a policy written
    /// before the AI switch was turned on, not to grade one.
    /// </summary>
    private static readonly Regex AiWords = new(
        @"\bAI\b|artificial intelligence|language model|\bLLMs?\b|machine learning|model provider|\bGemini\b|\bOpenAI\b|\bAnthropic\b|\bClaude\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool MentionsAi(string? markdown) => Exists(markdown) && AiWords.IsMatch(markdown!);

    /// <summary>
    /// What is wrong with the policy while AI automation is on, or null
    /// while nothing is: with the switch on, what members type — a brief,
    /// a profile, a note on an entry — leaves this server for the model
    /// provider, and a policy that does not say so is out of date. Shown
    /// on the AI automation group of the settings screen, beside the
    /// switch that makes it true.
    /// </summary>
    public static string? AiGap(bool aiEnabled, string? privacyMarkdown)
    {
        if (!aiEnabled) return null;
        const string consequence = " What members type — briefs, profiles, notes on entries — is sent to the model provider to draft from, and the policy should say so: Legal → Privacy policy.";
        if (!Exists(privacyMarkdown)) return "AI automation is on, but no privacy policy is published." + consequence;
        if (!MentionsAi(privacyMarkdown)) return "AI automation is on, but the privacy policy does not mention AI." + consequence;
        return null;
    }
}
