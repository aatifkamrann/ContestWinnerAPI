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
}
