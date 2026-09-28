namespace WinnersPortal.Services.Help;

/// <summary>
/// One piece of field-level guidance, served to the UI and rendered by the
/// help icon beside a field. Content lives here rather than in JSX so there
/// is exactly one source of truth and it can be reviewed in a pull request.
/// </summary>
/// <param name="Id">
/// Stable key. Settings use the convention <c>settings.&lt;setting key&gt;</c>;
/// other surfaces use their own prefix (<c>opportunity.</c>, <c>entry.</c>).
/// </param>
/// <param name="Short">One line for the tooltip. Keep under ~120 characters.</param>
/// <param name="Detail">The overlay body. Plain text; blank lines separate paragraphs.</param>
/// <param name="Why">
/// What breaks, costs money, or cannot be undone if this is wrong — the part
/// documentation usually omits.
/// </param>
/// <param name="Example">A concrete sample value, shown in a code style.</param>
/// <param name="LearnMoreUrl">Optional external reference.</param>
public sealed record HelpTopic(
    string Id,
    string Short,
    string? Detail = null,
    string? Why = null,
    string? Example = null,
    string? LearnMoreUrl = null);
