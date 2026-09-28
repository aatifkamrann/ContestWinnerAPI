using WinnersPortal.Services.Auth;
using System.Text.RegularExpressions;

namespace WinnersPortal.Services.GitHub;

/// <summary>
/// How an entrant claims a milestone from inside the repo, per the blueprint:
/// push a tag <c>m1</c>, <c>m2</c>… or open a pull request whose head branch
/// starts with the same token (<c>m2-invoices</c>) or whose title carries it
/// (<c>[m2] invoicing</c>). The number is 1-based on the board and maps to
/// Milestone.Order = number − 1.
/// </summary>
public static partial class MilestoneRefs
{
    [GeneratedRegex(@"^m([1-9]\d{0,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^m([1-9]\d{0,2})(?:[-_/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BranchPattern();

    [GeneratedRegex(@"[\[\(]m([1-9]\d{0,2})[\]\)]", RegexOptions.IgnoreCase)]
    private static partial Regex TitlePattern();

    /// <summary>"m2" or "refs/tags/m2" → milestone number 2. Null when the tag is not a claim.</summary>
    public static int? FromTag(string? refName)
    {
        if (string.IsNullOrEmpty(refName)) return null;
        const string prefix = "refs/tags/";
        var tag = refName.StartsWith(prefix, StringComparison.Ordinal) ? refName[prefix.Length..] : refName;
        var match = TagPattern().Match(tag);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>Branch "m2-invoices" or title "[m2] invoicing" → 2. Branch wins when both carry a number.</summary>
    public static int? FromPullRequest(string? headBranch, string? title)
    {
        if (!string.IsNullOrEmpty(headBranch))
        {
            var match = BranchPattern().Match(headBranch);
            if (match.Success) return int.Parse(match.Groups[1].Value);
        }
        if (!string.IsNullOrEmpty(title))
        {
            var match = TitlePattern().Match(title);
            if (match.Success) return int.Parse(match.Groups[1].Value);
        }
        return null;
    }
}
