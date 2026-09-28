using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>A technical requirement, one row of the opportunity's table: a name and what it asks for.</summary>
public sealed record RequirementInput(string? Title, string? Detail);

/// <summary>One line of the scoring rubric: how many points, for what, and how it is judged.</summary>
public sealed record CriterionInput(int? Points, string? Title, string? Description);

/// <summary>
/// The terms a client states beside the brief and that the page shows on
/// tabs of their own: the technical requirements the work must meet, the
/// rubric it is scored on, and the share of the whole each milestone
/// carries. All three are frozen at publish with the rest of the terms,
/// so the rules here are the door a draft goes through on the way.
/// </summary>
public static class Rubric
{
    public const int MaxRequirements = 20;
    public const int MaxCriteria = 10;
    public const int MaxTitle = Opportunity.MaxRubricTitle;
    public const int MaxDetail = Opportunity.MaxRubricDetail;
    public const int MaxPoints = 100;

    /// <summary>
    /// The requirements as stored: tidied, rows left wholly blank dropped.
    /// Null with a message when a row is half-filled or the table is too
    /// long — a name with nothing after it says nothing to an entrant.
    /// </summary>
    public static IReadOnlyList<(string Title, string Detail)>? CleanRequirements(
        IEnumerable<RequirementInput>? rows, out string? problem)
    {
        problem = null;
        var list = new List<(string, string)>();
        foreach (var row in rows ?? [])
        {
            var title = Tidy(row.Title);
            var detail = Tidy(row.Detail);
            if (title.Length == 0 && detail.Length == 0) continue;
            if (title.Length == 0 || detail.Length == 0)
            {
                problem = "Every technical requirement needs a name and what it asks for — "
                          + "\"Language\" and \"Python 3.11+\", say.";
                return null;
            }
            if (title.Length > MaxTitle || detail.Length > MaxDetail)
            {
                problem = $"Keep each requirement's name under {MaxTitle} characters and what it asks for under {MaxDetail}.";
                return null;
            }
            list.Add((title, detail));
        }
        if (list.Count > MaxRequirements)
        {
            problem = $"At most {MaxRequirements} technical requirements — the rest belongs in the brief.";
            return null;
        }
        return list;
    }

    /// <summary>
    /// The rubric as stored: tidied, rows left wholly blank dropped. Null
    /// with a message when a row has points and no name, a name and no
    /// points, or points off the scale.
    /// </summary>
    public static IReadOnlyList<(int Points, string Title, string? Description)>? CleanCriteria(
        IEnumerable<CriterionInput>? rows, out string? problem)
    {
        problem = null;
        var list = new List<(int, string, string?)>();
        foreach (var row in rows ?? [])
        {
            var title = Tidy(row.Title);
            var description = Tidy(row.Description);
            if (title.Length == 0 && description.Length == 0 && row.Points is null) continue;
            if (title.Length == 0)
            {
                problem = "Every scoring criterion needs a name — what the points are for.";
                return null;
            }
            if (row.Points is null or < 1 or > MaxPoints)
            {
                problem = $"Give “{title}” a whole number of points, from 1 to {MaxPoints}.";
                return null;
            }
            if (title.Length > MaxTitle || description.Length > MaxDetail)
            {
                problem = $"Keep each criterion's name under {MaxTitle} characters and its description under {MaxDetail}.";
                return null;
            }
            list.Add((row.Points.Value, title, description.Length == 0 ? null : description));
        }
        if (list.Count > MaxCriteria)
        {
            problem = $"At most {MaxCriteria} scoring criteria — fewer, weightier lines read better.";
            return null;
        }
        return list;
    }

    /// <summary>
    /// A draft may leave a milestone's share blank, or give it any whole
    /// percentage; only the impossible ones are refused here. Whether the
    /// shares add up is a publish question, because a list is edited one
    /// row at a time.
    /// </summary>
    public static string? WeightProblem(IEnumerable<int?> weights) =>
        weights.Any(w => w is < 1 or > 100)
            ? "A milestone's share of the work is a whole number from 1 to 100 percent."
            : null;

    /// <summary>
    /// At publish the shares are either not stated at all — a checklist
    /// with no percentages, as every board was before them — or stated on
    /// every milestone and adding up to the whole. Anything between would
    /// leave the board describing a fraction of the work.
    /// </summary>
    public static string? WeightPublishProblem(IReadOnlyList<int?> weights)
    {
        var set = weights.Count(w => w is not null);
        if (set == 0) return null;
        if (set < weights.Count)
            return "Either give every milestone its share of the work, or leave every share blank.";
        var total = weights.Sum(w => w!.Value);
        return total == 100
            ? null
            : $"The milestone shares add up to {total}%, not 100%. Adjust them, or leave every share blank.";
    }

    private static string Tidy(string? s) =>
        string.Join(' ', (s ?? "").Split(default(char[]), StringSplitOptions.RemoveEmptyEntries)).Trim();
}
