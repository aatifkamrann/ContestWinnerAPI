namespace WinnersPortal.Services.GitHub;

/// <summary>
/// Names for the org-owned entry repositories: opportunity slug + entrant
/// username, trimmed to GitHub's 100-character repo-name limit with the
/// username kept whole — it is the disambiguator when a slug is truncated.
/// </summary>
public static class RepoNames
{
    public const int MaxLength = 100;

    public static string For(string opportunitySlug, string githubUsername)
    {
        var username = githubUsername.ToLowerInvariant();
        var budget = MaxLength - username.Length - 1;
        var slug = opportunitySlug.Length <= budget ? opportunitySlug : opportunitySlug[..budget].TrimEnd('-');
        return $"{slug}-{username}";
    }
}
