using System.Text.RegularExpressions;
using WinnersPortal.Services.Profiles;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// One entrant's standing on one opportunity: a number out of 100 that puts the
/// best-placed row first, and the arithmetic behind it, line by line.
///
/// Nine parts in three groups. What they are doing here — milestones met on
/// time, milestones claimed, whether they keep working, and what the work
/// itself shows where the portal can read it — is half the score, because an
/// opportunity is decided on what was built for it. Their record elsewhere —
/// opportunities finished, the win ratio, ratings from clients — is the next
/// quarter. What they wrote about themselves — the portfolio and the skills
/// that match this brief — is the last quarter, and the one an afternoon of
/// typing can move.
///
/// It decides nothing. It orders the board and sits beside the client's
/// remove button as a reading, but the client keeps or removes an entrant,
/// and picks the winner by reading the work. A part the portal cannot read
/// yet — a repository with no pushes, an opportunity whose dated milestones have
/// not come due — is left out of the denominator rather than scored as a
/// miss, so two entrants are compared on what is actually known about both.
/// </summary>
public static class Standing
{
    public const int OnTimeMax = 20;
    public const int ProgressMax = 10;
    public const int ActivityMax = 10;
    public const int QualityMax = 10;
    /// <summary>Only on an opportunity that requires Docker Compose — the tenth part, there.</summary>
    public const int BuildsMax = 10;
    public const int FinishedMax = 10;
    public const int WinsMax = 10;
    public const int RatingsMax = 5;
    public const int PortfolioMax = 15;
    public const int SkillsMax = 10;

    /// <summary>The parts about this opportunity — the half that decides the reading.</summary>
    private static readonly HashSet<string> HereKeys = ["onTime", "progress", "activity", "quality", "builds"];

    /// <summary>
    /// The keep-or-remove reading. Wide bands on purpose: the score is a
    /// number to compare rows by, the band is one word for whether this
    /// row needs the client's attention at all.
    /// </summary>
    public enum Band
    {
        /// <summary>Nothing to read yet — just entered, nothing due, nothing pushed.</summary>
        TooEarly = 0,
        OnTrack = 1,
        Behind = 2,
        AtRisk = 3,
    }

    /// <summary>What the repository's file listing showed, the last time the worker read it.</summary>
    public sealed record RepoFacts(int FileCount, bool HasTests, bool HasReadme, bool HasCi, bool HasCompose = false);

    /// <summary>
    /// Everything the score reads. Counted by the portal — nothing here is
    /// typed in by the entrant except the last three, and those are the
    /// smallest quarter.
    /// </summary>
    public sealed record Facts(
        Schedule.Standing Board,
        int Milestones,
        int DatedMilestones,
        DateTimeOffset EnteredAtUtc,
        DateTimeOffset? LastActivityUtc,
        /// <summary>Now — or the deadline once it has passed, so a frozen row is read as it stood.</summary>
        DateTimeOffset AsOfUtc,
        bool Frozen,
        bool UsesRepository,
        RepoFacts? Repo,
        bool UsesUpload,
        int FilesUploaded,
        int DocumentsUploaded,
        int OpportunitiesDecided,
        int OpportunitiesWon,
        int RatingCount,
        int RatingSum,
        int PortfolioScore,
        int SkillsListed,
        IReadOnlyList<string> SkillsMatched,
        /// <summary>The opportunity requires Docker Compose, so each claim's build is a part of the score.</summary>
        bool RequiresCompose = false,
        /// <summary>Claims whose build the host has answered, built or failed; a build still running is not counted.</summary>
        int BuildsFinished = 0,
        int BuildsOk = 0);

    /// <summary>One line of the breakdown. Available is zero when the portal cannot read this part yet.</summary>
    public sealed record Part(string Key, string Label, int Earned, int Available, string Detail);

    public static IReadOnlyList<Part> Parts(Facts f)
    {
        var b = f.Board;
        var parts = new List<Part>(10);

        // ---- here: what they are doing on this opportunity ------------------
        if (f.DatedMilestones == 0)
            parts.Add(new("onTime", "Milestones on time", 0, 0,
                "This opportunity puts no dates on its milestones, so there is no on-time to read."));
        else if (b.Dated == 0)
            parts.Add(new("onTime", "Milestones on time", 0, 0,
                "No dated milestone has come due yet."));
        else
            parts.Add(new("onTime", "Milestones on time",
                (int)Math.Round(OnTimeMax * (b.OnTime + 0.5 * b.Late) / b.Dated), OnTimeMax,
                $"{b.OnTime} of {b.Dated} dated milestone{Plural(b.Dated)} met on time"
                + (b.Late > 0 ? $", {b.Late} late" : "")
                + (b.Overdue > 0 ? $", {b.Overdue} overdue" : "") + "."));

        if (f.Milestones == 0)
            parts.Add(new("progress", "Milestones claimed", 0, 0, "This opportunity has no milestones."));
        else
            parts.Add(new("progress", "Milestones claimed",
                (int)Math.Round(ProgressMax * (double)b.Done / f.Milestones), ProgressMax,
                $"{b.Done} of {f.Milestones} claimed."));

        if (f.LastActivityUtc is null)
            parts.Add(new("activity", "Keeps working", 0, ActivityMax,
                f.Frozen ? "Nothing was pushed or uploaded before the deadline." : "Nothing pushed or uploaded yet."));
        else
        {
            var days = Math.Max(0, (f.AsOfUtc - f.LastActivityUtc.Value).TotalDays);
            var earned = days <= 3 ? ActivityMax : days <= 7 ? 7 : days <= 14 ? 4 : 2;
            parts.Add(new("activity", "Keeps working", earned, ActivityMax,
                f.Frozen ? $"Last activity {Days(days)} before the deadline." : $"Last activity {Days(days)} ago."));
        }

        if (f.UsesRepository && f.Repo is { } r)
        {
            var has = new List<string>();
            var missing = new List<string>();
            (r.HasTests ? has : missing).Add("tests");
            (r.HasReadme ? has : missing).Add("a README");
            (r.HasCi ? has : missing).Add("CI");
            var earned = (r.HasTests ? 4 : 0) + (r.HasReadme ? 3 : 0) + (r.HasCi ? 2 : 0) + (r.FileCount >= 5 ? 1 : 0);
            var files = $"{r.FileCount} file{Plural(r.FileCount)}";
            parts.Add(new("quality", "Code & docs", earned, QualityMax,
                has.Count == 0
                    ? $"Repository read: no tests, no README, no CI — {files}."
                    : missing.Count == 0
                        ? $"Repository read: {Join(has)} — {files}."
                        : $"Repository read: {Join(has)}; no {Join(missing)} — {files}."));
        }
        else if (f.UsesUpload)
        {
            var earned = (f.FilesUploaded > 0 ? 5 : 0) + (f.DocumentsUploaded > 0 ? 5 : 0);
            parts.Add(new("quality", "Code & docs", earned, QualityMax,
                f.FilesUploaded == 0
                    ? "Nothing handed in yet."
                    : $"{f.FilesUploaded} file{Plural(f.FilesUploaded)} handed in"
                        + (f.DocumentsUploaded > 0
                            ? ", with a document explaining the work."
                            : ", but no document explaining the work.")));
        }
        else
        {
            parts.Add(new("quality", "Code & docs", 0, 0,
                "Not readable yet — the repository has had no pushes, or this portal cannot read it."));
        }

        // ---- the build of each claim, where the opportunity asks for one -------
        // Available only once a claim has finished building: a row nothing
        // has been built for yet is not marked down, and an opportunity without
        // the rule never carries the part at all.
        if (f.RequiresCompose)
        {
            if (f.BuildsFinished == 0)
                parts.Add(new("builds", "Builds", 0, 0,
                    b.Done == 0 ? "No milestone claimed yet, so nothing has been built." : "No claim has finished building yet."));
            else
            {
                var earned = (int)Math.Round((double)BuildsMax * f.BuildsOk / f.BuildsFinished);
                parts.Add(new("builds", "Builds", earned, BuildsMax,
                    f.BuildsOk == f.BuildsFinished
                        ? $"Every claimed milestone builds ({f.BuildsFinished} of {f.BuildsFinished})."
                        : $"{f.BuildsOk} of {f.BuildsFinished} claimed milestone{Plural(f.BuildsFinished)} build{(f.BuildsFinished == 1 ? "s" : "")}."));
            }
        }

        // ---- their record elsewhere ---------------------------------------
        var finished = f.OpportunitiesDecided switch { 0 => 0, 1 => 4, 2 => 7, _ => FinishedMax };
        parts.Add(new("finished", "Opportunities finished", finished, FinishedMax,
            f.OpportunitiesDecided == 0
                ? "No decided opportunity yet — this would be the first they stayed in to the end."
                : $"{f.OpportunitiesDecided} {(f.OpportunitiesDecided == 1 ? "opportunity" : "opportunities")} elsewhere, stayed in to the decision."));

        if (f.OpportunitiesDecided == 0)
            parts.Add(new("wins", "Win ratio", 0, WinsMax, "Nothing decided yet."));
        else
        {
            // The ratio carries the quality, the count the confidence: one
            // win in one opportunity is not a hundred percent of anything.
            var earned = (int)Math.Round(
                WinsMax * ((double)f.OpportunitiesWon / f.OpportunitiesDecided) * Math.Min(f.OpportunitiesDecided, 3) / 3.0);
            parts.Add(new("wins", "Win ratio", earned, WinsMax,
                $"{f.OpportunitiesWon} of {f.OpportunitiesDecided} won"
                + (f.OpportunitiesDecided < 3 ? " — a small sample, so it counts for less." : ".")));
        }

        if (f.RatingCount == 0)
            parts.Add(new("ratings", "Ratings from clients", 0, RatingsMax,
                "No ratings yet — they come from paid awards."));
        else
        {
            var avg = (double)f.RatingSum / f.RatingCount;
            var earned = (int)Math.Round(RatingsMax * (avg / 5.0) * Math.Min(f.RatingCount, 3) / 3.0);
            parts.Add(new("ratings", "Ratings from clients", earned, RatingsMax,
                $"{avg:0.0} stars from {f.RatingCount} client{Plural(f.RatingCount)}"
                + (f.RatingCount < 3 ? " — still a small sample." : ".")));
        }

        // ---- what they wrote --------------------------------------------
        parts.Add(new("portfolio", "Portfolio",
            (int)Math.Round(PortfolioMax * (double)f.PortfolioScore / Merit.PortfolioMax), PortfolioMax,
            $"{f.PortfolioScore} of {Merit.PortfolioMax} on the written half of their merit score."));

        if (f.SkillsListed == 0)
            parts.Add(new("skills", "Skills for this brief", 0, SkillsMax, "No skills listed on their profile."));
        else if (f.SkillsMatched.Count == 0)
            parts.Add(new("skills", "Skills for this brief", 0, SkillsMax,
                $"None of the {f.SkillsListed} skill{Plural(f.SkillsListed)} they list appears in the brief."));
        else
            parts.Add(new("skills", "Skills for this brief",
                f.SkillsMatched.Count switch { 1 => 5, 2 => 8, _ => SkillsMax }, SkillsMax,
                $"{f.SkillsMatched.Count} listed skill{Plural(f.SkillsMatched.Count)} in the brief: "
                + string.Join(", ", f.SkillsMatched) + "."));

        return parts;
    }

    /// <summary>Earned over what could be read, scaled to 100. Zero when nothing is readable at all.</summary>
    public static int Score(IReadOnlyList<Part> parts)
    {
        var available = parts.Sum(p => p.Available);
        return available == 0 ? 0 : (int)Math.Round(100.0 * parts.Sum(p => p.Earned) / available);
    }

    /// <summary>
    /// The reading, from the parts about this opportunity only: a strong record
    /// elsewhere does not make a row that has gone quiet here "on track".
    /// </summary>
    public static Band BandOf(Facts f, IReadOnlyList<Part> parts)
    {
        var b = f.Board;
        if (b.Done == 0 && b.Overdue == 0 && f.LastActivityUtc is null
            && f.AsOfUtc - f.EnteredAtUtc < TimeSpan.FromDays(2))
            return Band.TooEarly;
        // Two dates gone by with nothing claimed is the pattern removal
        // usually follows, whatever else the row says.
        if (b.Overdue >= 2 && b.Done == 0) return Band.AtRisk;

        var here = parts.Where(p => HereKeys.Contains(p.Key)).ToList();
        var available = here.Sum(p => p.Available);
        if (available == 0) return Band.TooEarly;
        var ratio = (double)here.Sum(p => p.Earned) / available;
        return ratio >= 0.6 ? Band.OnTrack : ratio >= 0.3 ? Band.Behind : Band.AtRisk;
    }

    public static string BandName(Band band) => band switch
    {
        Band.OnTrack => "on_track",
        Band.Behind => "behind",
        Band.AtRisk => "at_risk",
        _ => "too_early",
    };

    /// <summary>
    /// The one thing that would move this row up most, for the entrant's own
    /// box. This opportunity's parts first — they are the ones that can change
    /// today — and null when every readable part is full.
    /// </summary>
    public static string? NextStep(Facts f, IReadOnlyList<Part> parts)
    {
        var candidates = parts
            .Where(p => p.Available > 0 && p.Earned < p.Available)
            .OrderByDescending(p => HereKeys.Contains(p.Key))
            .ThenByDescending(p => p.Available - p.Earned);
        foreach (var p in candidates)
        {
            var hint = p.Key switch
            {
                "onTime" => "Claim the next milestone on or before its date — on-time claims are the biggest part of the score.",
                "progress" => "Claim the next milestone; every claim moves this row up.",
                "activity" => "Push or upload something — a quiet row reads as a stall.",
                "builds" => "Fix the build — the red mark on the board opens its log.",
                "quality" => f.UsesRepository && f.Repo is not null
                    ? "Add what the repository is missing — tests, a README, CI."
                    : "Hand in a document alongside the files, so the client can read what was built.",
                "portfolio" => "Finish your profile — the written half of your merit score counts here.",
                "skills" => "List the skills this brief asks for on your profile, if you have them.",
                _ => null, // earned on finished opportunities; nothing to do today
            };
            if (hint is not null) return hint;
        }
        return null;
    }

    /// <summary>
    /// Which of the listed skills the brief actually names, as whole words —
    /// "React" in "React Native" counts, "React" in "reactive" does not.
    /// Case-insensitive; a skill is matched once however often it appears.
    /// </summary>
    public static IReadOnlyList<string> MatchSkills(IEnumerable<string> skills, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var matched = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in skills)
        {
            var skill = raw?.Trim();
            if (string.IsNullOrEmpty(skill) || !seen.Add(skill)) continue;
            var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(skill) + @"(?![\p{L}\p{N}])";
            if (Regex.IsMatch(text, pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
                matched.Add(skill);
        }
        return matched;
    }

    /// <summary>A file that explains work rather than being it: notes, a brief, a PDF.</summary>
    public static bool IsDocument(string? contentType, string? fileName)
    {
        var type = (contentType ?? "").ToLowerInvariant();
        if (type.StartsWith("text/") || type is "application/pdf" or "application/rtf" or "application/msword"
            || type == "application/vnd.openxmlformats-officedocument.wordprocessingml.document")
            return true;
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext is ".md" or ".txt" or ".pdf" or ".doc" or ".docx" or ".rtf";
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    private static string Days(double days) =>
        days < 1 ? "less than a day" : $"{(int)Math.Floor(days)} day{Plural((int)Math.Floor(days))}";

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
