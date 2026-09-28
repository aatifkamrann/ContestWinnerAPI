using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// Validation for the rating form — pure so the rules are testable without a
/// database and identical wherever they are enforced.
/// </summary>
public static class RatingRules
{
    public const int MaxComment = Rating.MaxComment;

    /// <summary>Null when the rating is acceptable, else the message the form shows.</summary>
    public static string? Problem(int stars, string? comment) =>
        stars is < 1 or > 5
            ? "A rating is one to five stars."
            : comment is { Length: > MaxComment }
                ? $"Keep the comment under {MaxComment} characters — it is shown wherever the score is."
                : null;

    /// <summary>Trimmed comment, or null when there is nothing worth storing.</summary>
    public static string? CleanComment(string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
}

/// <summary>
/// The arithmetic behind a client's public payment record. The record is the
/// counterweight to "no deposit, open entry": entrants stake real work on a
/// promise, so the portal shows exactly how that client's promises have gone.
/// </summary>
public static class TrackRecordMath
{
    /// <summary>
    /// Median, not mean — one award paid after a 90-day dispute must not
    /// drown five same-day payments, and vice versa. Null when empty.
    /// </summary>
    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Whole days between announce and pay, floored — "paid in 0 days" is same-day.</summary>
    public static double DaysToPay(DateTimeOffset announcedAtUtc, DateTimeOffset paidAtUtc) =>
        Math.Max(0, Math.Floor((paidAtUtc - announcedAtUtc).TotalDays));
}
