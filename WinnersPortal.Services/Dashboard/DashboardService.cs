using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Opportunities;
using WinnersPortal.Services.GitHub;

namespace WinnersPortal.Services.Dashboard;

/// <summary>
/// The signed-in landing screen, shaped by role: a client sees the money and
/// the entrants their opportunities attracted, a freelancer sees their own progress
/// and earnings, an administrator sees portal health plus both of those views
/// aggregated across everyone.
///
/// One endpoint, one HTTP round trip per dashboard. The blueprint's budget is
/// two hundred milliseconds for a whole view, and a dozen cards each fetching
/// their own numbers is exactly how that gets spent — so every panel below is
/// filled from a single response. The queries behind it are indexed aggregates
/// and narrow projections; the daily buckets are counted in memory over a
/// thirty-day window, which is one round trip and a single column rather than
/// a date-truncating GROUP BY per series.
///
/// Nothing here is authoritative — every number is a projection of rows the
/// opportunity, entry, award and webhook flows already wrote. A dashboard that
/// keeps its own totals is a dashboard that drifts.
/// </summary>
public sealed partial class DashboardService(AppDbContext db, GitHubService github)
{
    /// <summary>The activity window every chart shares, in days.</summary>
    private const int WindowDays = 30;

    public async Task<Outcome<DashboardResponse>> ReadAsync(string? tz, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = Principal.UserId(principal)!.Value;
        var now = DateTimeOffset.UtcNow;
        // The charts count days where the viewer is. A point on a chart
        // opens a report narrowed to its day, and a report's dates are the
        // viewer's own, so the two have to mean the same day: the browser
        // sends its zone, and a zone this server cannot find counts in UTC.
        var zone = ZoneOf(tz);
        var from = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime).AddDays(-(WindowDays - 1));
        var since = StartOf(from, zone);

        return principal.IsInRole(Roles.Admin)
            ? Outcome.Ok<DashboardResponse>(await AdminAsync(db, now, from, since, zone, ct))
            : principal.IsInRole(Roles.Client)
                ? Outcome.Ok<DashboardResponse>(await ClientAsync(db, github, userId, now, from, since, zone, ct))
                : Outcome.Ok<DashboardResponse>(await FreelancerAsync(db, userId, now, from, since, zone, ct));
    }

    // =====================================================================
    // Client — the money, the entrants, and what is waiting on a decision
    // =====================================================================

    private static async Task<ClientDashboard> ClientAsync(
        AppDbContext db, GitHubService github, Guid userId, DateTimeOffset now, DateOnly from, DateTimeOffset since,
        TimeZoneInfo zone, CancellationToken ct)
    {
        // The seven reads, in the LINQ or the T-SQL; the newest two hundred
        // opportunities and everything hanging off them.
        var (opportunities, entryStamps, claimStamps, claimsByOpportunity, awards, recent, failed) = db.UseDapper
            ? await ClientReadsSqlAsync(db.Sql, userId, since, ct)
            : await ClientReadsLinqAsync(db, userId, since, ct);

        // Every live board's standing, so each progress row can say who
        // leads it and how many rows read "at risk" — the same numbers the
        // opportunity page shows, batched here for every opportunity at once.
        var standings = await StandingReader.ForOpportunitiesAsync(
            db,
            opportunities.Where(c => c.Status is OpportunityStatus.Open or OpportunityStatus.Reviewing).Select(c => c.Id).ToList(),
            now, ct);
        var leaders = standings.Values
            .GroupBy(r => r.OpportunityId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Rank).First());
        var atRiskByOpportunity = standings.Values
            .Where(r => r.Band == Standing.Band.AtRisk)
            .GroupBy(r => r.OpportunityId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Applications still to decide on this client's open opportunities.
        var waiting = await WaitingApplicationsAsync(db, userId, ct);

        // ---- what needs the client's attention, most urgent first --------
        var awardedOpportunityIds = awards.Select(a => a.OpportunityId).ToHashSet();
        var attention = new List<AttentionItem>();

        foreach (var a in awards.Where(a => a.PaidAtUtc is null))
            attention.Add(Item("award_unpaid", "danger",
                $"Pay the award for “{a.Title}”",
                $"{a.Winner} was announced {Ago(now, a.AnnouncedAtUtc)}. The repository transfers when you mark it paid.",
                $"/opportunities/{a.Slug}"));

        // Where the reading happens depends on the portal: with the GitHub
        // App the client reads the code in GitHub; without it the opportunity
        // page is the whole review, and the hint says so in that page's words.
        var githubConfigured = await github.IsConfiguredAsync(ct);
        foreach (var c in opportunities.Where(c => c.Status == OpportunityStatus.Reviewing && !awardedOpportunityIds.Contains(c.Id)))
            attention.Add(Item("announce", "warn",
                $"Choose a winner for “{c.Title}”",
                githubConfigured
                    ? $"{c.Entrants} {(c.Entrants == 1 ? "entry is" : "entries are")} frozen and readable — the review happens in GitHub, the verdict here."
                    : $"{c.Entrants} {(c.Entrants == 1 ? "entry is" : "entries are")} frozen at the final tag — compare them on the opportunity page and announce the winner there.",
                $"/opportunities/{c.Slug}"));

        // Applicants waiting to hear whether they compete — each is keeping
        // the weeks of this timeline free meanwhile.
        foreach (var w in waiting)
            attention.Add(Item("applications", "warn",
                $"Decide {w.Count} application{(w.Count == 1 ? "" : "s")} on “{w.Title}”",
                $"The oldest was filed {Ago(now, w.Oldest)}. Read each approach and the past work attached, then select or not on the opportunity page.",
                $"/opportunities/{w.Slug}"));

        // Rows reading "at risk" — overdue milestones with nothing claimed, or
        // a row gone quiet. The board is where the decision is made; this
        // only says which board to open.
        foreach (var c in opportunities.Where(c => c.Status is OpportunityStatus.Open or OpportunityStatus.Reviewing))
        {
            var atRisk = atRiskByOpportunity.GetValueOrDefault(c.Id);
            if (atRisk == 0) continue;
            attention.Add(Item("at_risk", "warn",
                $"{atRisk} entrant{(atRisk == 1 ? "" : "s")} at risk on “{c.Title}”",
                "Overdue milestones with nothing claimed, or a row gone quiet — read the board and their work before deciding whether they stay in.",
                $"/opportunities/{c.Slug}"));
        }

        foreach (var a in awards.Where(a => a.PaidAtUtc is not null && a.Handover == HandoverStatus.Requested))
            attention.Add(Item("handover", "warn",
                $"Transfer pending for “{a.Title}”",
                $"@{a.TransferTargetLogin} has not accepted the repository yet — a transfer to a personal account waits for the recipient.",
                $"/opportunities/{a.Slug}"));

        foreach (var f in failed)
            attention.Add(Item("repo_failed", "danger",
                $"A repository could not be created on “{f.Title}”",
                $"The entry from {f.GithubUsername} has no repo, so that entrant cannot start. An administrator can see why.",
                $"/opportunities/{f.Slug}"));

        // The headline sums what the progress panel lists: opportunities open, in
        // review or awarded. A cancelled opportunity's entrants and claims are
        // history — still in the mix and the feed — but a "possible"
        // milestone is one that can still be claimed, and the per-opportunity
        // average must divide the same entrants it counts.
        var live = opportunities.Where(c => c.Status != OpportunityStatus.Draft && c.Status != OpportunityStatus.Cancelled).ToList();
        var liveIds = live.Select(c => c.Id).ToHashSet();
        var published = live.Count;

        return new ClientDashboard
        {
            Role = "client",
            GeneratedAtUtc = now,
            Headline = new ClientHeadline
            {
                LiveOpportunities = opportunities.Count(c => c.Status == OpportunityStatus.Open),
                InReview = opportunities.Count(c => c.Status == OpportunityStatus.Reviewing),
                Awarded = opportunities.Count(c => c.Status == OpportunityStatus.Awarded),
                Drafts = opportunities.Count(c => c.Status == OpportunityStatus.Draft),
                Entrants = live.Sum(c => c.Entrants),
                AvgEntrants = published == 0 ? 0 : Math.Round((double)live.Sum(c => c.Entrants) / published, 1),
                MilestonesClaimed = claimsByOpportunity.Where(kv => liveIds.Contains(kv.Key)).Sum(kv => kv.Value),
                MilestonesPossible = live.Sum(c => c.Entrants * c.Milestones),
                RepoIssues = opportunities.Sum(c => c.RepoIssues),
                ApplicationsWaiting = waiting.Sum(w => w.Count),
            },
            // One row per open opportunity with applications under review, the
            // longest-waiting first — the tile links straight to a lone one.
            Waiting = waiting
                .Select(w => new WaitingReview { Slug = w.Slug, Title = w.Title, Count = w.Count, OldestUtc = w.Oldest }),
            Money = Money(awards.Select(a => (a.Currency, a.Amount, a.PaidAtUtc))),
            Activity = new DailyChart
            {
                Labels = Labels(from, WindowDays),
                Series = new ChartSeries[]
                {
                    new ChartSeries { Name = "Entries received", Points = Bucket(entryStamps, from, WindowDays, zone) },
                    new ChartSeries { Name = "Milestones claimed", Points = Bucket(claimStamps, from, WindowDays, zone) },
                },
            },
            StatusSplit = Split(
                ("Open", opportunities.Count(c => c.Status == OpportunityStatus.Open)),
                ("In review", opportunities.Count(c => c.Status == OpportunityStatus.Reviewing)),
                ("Awarded", opportunities.Count(c => c.Status == OpportunityStatus.Awarded)),
                ("Draft", opportunities.Count(c => c.Status == OpportunityStatus.Draft)),
                ("Cancelled", opportunities.Count(c => c.Status == OpportunityStatus.Cancelled))),
            Progress = opportunities
                .Where(c => c.Status is OpportunityStatus.Open or OpportunityStatus.Reviewing or OpportunityStatus.Awarded)
                .OrderByDescending(c => c.Entrants)
                .Take(6)
                .Select(c => new ClientOpportunityProgress
                {
                    Slug = c.Slug,
                    Title = c.Title,
                    Status = OpportunityNames.StatusName(c.Status),
                    DeadlineUtc = c.DeadlineUtc,
                    Entrants = c.Entrants,
                    Claimed = claimsByOpportunity.GetValueOrDefault(c.Id),
                    Possible = c.Entrants * c.Milestones,
                    // Who leads the board, and how many rows need a look.
                    Leader = leaders.TryGetValue(c.Id, out var lead)
                        ? new ProgressLeader { Name = lead.DisplayName, Score = lead.Score }
                        : null,
                    AtRisk = atRiskByOpportunity.GetValueOrDefault(c.Id),
                }),
            Attention = attention,
            Deadlines = opportunities
                .Where(c => c.Status == OpportunityStatus.Open && c.DeadlineUtc != null)
                .OrderBy(c => c.DeadlineUtc)
                .Take(5)
                .Select(c => new ClientDeadline { Slug = c.Slug, Title = c.Title, DeadlineUtc = c.DeadlineUtc, Entrants = c.Entrants }),
            Recent = recent.Select(r => new RecentEvent
            {
                AtUtc = r.ClaimedAtUtc,
                Text = $"{r.Who} claimed milestone {r.Order + 1} — {r.Milestone}",
                Detail = r.Via == "tag" ? "pushed a tag" : "opened a pull request",
                Href = $"/opportunities/{r.Slug}",
            }),
        };
    }

    // =====================================================================
    // Freelancer — own progress, own earnings, own deadlines
    // =====================================================================

    private static async Task<FreelancerDashboard> FreelancerAsync(
        AppDbContext db, Guid userId, DateTimeOffset now, DateOnly from, DateTimeOffset since, TimeZoneInfo zone,
        CancellationToken ct)
    {
        // The four reads, in the LINQ or the T-SQL; the newest two hundred
        // entries and everything hanging off them.
        var (entries, claimStamps, awards, recent) = db.UseDapper
            ? await FreelancerReadsSqlAsync(db.Sql, userId, since, ct)
            : await FreelancerReadsLinqAsync(db, userId, since, ct);

        var wonEntryIds = awards.Select(a => a.EntryId).ToHashSet();
        // Withdrawn entries are excluded from the win rate on both sides: you
        // took yourself out of the running, so counting the opportunity as one you
        // lost would understate every rate for an honest reason.
        var contested = entries.Where(e => e.Status == EntryStatus.Active).ToList();
        var decided = contested.Count(e => e.OpportunityStatus == OpportunityStatus.Awarded);
        var wins = contested.Count(e => wonEntryIds.Contains(e.Id));

        // Where each live entry stands on its board, from the same reader the
        // opportunity page uses — their own row only; the list never shows one
        // entrant another's standing here.
        var standings = await StandingReader.ForOpportunitiesAsync(
            db,
            contested.Where(e => e.OpportunityStatus is OpportunityStatus.Open or OpportunityStatus.Reviewing)
                .Select(e => e.OpportunityId).Distinct().ToList(),
            now, ct);

        // ---- what needs the freelancer's attention -----------------------
        var attention = new List<AttentionItem>();

        foreach (var a in awards.Where(a => a.PaidAtUtc is not null && a.Handover == HandoverStatus.Requested))
            attention.Add(Item("handover", "warn",
                $"Accept the repository for “{a.Title}”",
                "The award is paid and the transfer is waiting on GitHub — accept it there and the repo is yours.",
                $"/opportunities/{a.Slug}"));

        foreach (var a in awards.Where(a => a.PaidAtUtc is null))
            attention.Add(Item("award_unpaid", "info",
                $"You won “{a.Title}” — payment pending",
                $"Announced {Ago(now, a.AnnouncedAtUtc)}. The repository transfers to the client once they confirm payment.",
                $"/opportunities/{a.Slug}"));

        foreach (var e in contested.Where(e => e.ProvisionStatus == RepoProvisionStatus.Failed))
            attention.Add(Item("repo_failed", "danger",
                $"Your repository for “{e.Title}” could not be created",
                "Nothing you did — an administrator has the reason. Your entry still stands.",
                $"/opportunities/{e.Slug}"));

        foreach (var e in contested
                     .Where(e => e.OpportunityStatus == OpportunityStatus.Open && e.Claimed == 0 && e.Milestones > 0)
                     .Where(e => e.DeadlineUtc != null && e.DeadlineUtc <= now.AddDays(7)))
            attention.Add(Item("no_claims", "warn",
                $"No milestones claimed on “{e.Title}”",
                $"Entry closes {Ago(now, e.DeadlineUtc!.Value, future: true)}. Push a tag m1 to put your first checkpoint on the board.",
                $"/opportunities/{e.Slug}"));

        return new FreelancerDashboard
        {
            Role = "freelancer",
            GeneratedAtUtc = now,
            Headline = new FreelancerHeadline
            {
                ActiveEntries = contested.Count(e => e.OpportunityStatus is OpportunityStatus.Open or OpportunityStatus.Reviewing),
                TotalEntries = entries.Count,
                MilestonesClaimed = entries.Sum(e => e.Claimed),
                MilestonesPossible = entries.Sum(e => e.Milestones),
                Wins = wins,
                Decided = decided,
                WinRate = decided == 0 ? 0 : Math.Round(100.0 * wins / decided),
                Pushes = entries.Sum(e => e.PushCount),
                RepoIssues = contested.Count(e => e.ProvisionStatus == RepoProvisionStatus.Failed),
            },
            Money = Money(awards.Select(a => (a.Currency, a.Amount, a.PaidAtUtc))),
            Activity = new DailyChart
            {
                Labels = Labels(from, WindowDays),
                Series = new ChartSeries[]
                {
                    new ChartSeries { Name = "Milestones claimed", Points = Bucket(claimStamps, from, WindowDays, zone) },
                    new ChartSeries
                    {
                        Name = "Opportunities entered",
                        Points = Bucket(entries.Where(e => e.CreatedAtUtc >= since).Select(e => e.CreatedAtUtc), from, WindowDays, zone),
                    },
                },
            },
            // Mutually exclusive by construction, so the slices sum to the
            // entry count: every other slice reads from "contested", which
            // already excludes entries that left. An entry withdrawn from an
            // opportunity later awarded to someone else is withdrawn, not lost;
            // an entry the client removed is its own slice, because leaving
            // and being asked to leave are not the same outcome.
            OutcomeSplit = Split(
                ("In progress", contested.Count(e => e.OpportunityStatus is OpportunityStatus.Open or OpportunityStatus.Reviewing)),
                ("Won", wins),
                ("Not selected", contested.Count(e => e.OpportunityStatus == OpportunityStatus.Awarded && !wonEntryIds.Contains(e.Id))),
                ("Withdrawn", entries.Count(e => e.Status == EntryStatus.Withdrawn)),
                ("Removed", entries.Count(e => e.Status == EntryStatus.Removed)),
                ("Selection taken back", entries.Count(e => e.Status == EntryStatus.Deselected)),
                ("Cancelled", contested.Count(e => e.OpportunityStatus == OpportunityStatus.Cancelled))),
            Progress = contested
                .Where(e => e.OpportunityStatus is OpportunityStatus.Open or OpportunityStatus.Reviewing)
                .OrderBy(e => e.DeadlineUtc ?? DateTimeOffset.MaxValue)
                .Take(6)
                .Select(e => new FreelancerEntryProgress
                {
                    Slug = e.Slug,
                    Title = e.Title,
                    Status = OpportunityNames.StatusName(e.OpportunityStatus),
                    DeadlineUtc = e.DeadlineUtc,
                    Claimed = e.Claimed,
                    Possible = e.Milestones,
                    // Named only once the entrant can open it; see the entries list.
                    RepoFullName = e.ProvisionStatus == RepoProvisionStatus.Provisioned ? e.RepoFullName : null,
                    // So the row never says "repo pending" about an opportunity with no repository.
                    Delivery = Delivery.Name(e.Delivery),
                    LastPushAtUtc = e.LastPushAtUtc,
                    AwardAmount = e.AwardAmount,
                    Currency = e.Currency,
                    Standing = standings.TryGetValue(e.Id, out var st)
                        ? new StandingSummary { Score = st.Score, Band = Standing.BandName(st.Band), Rank = st.Rank, Of = st.Of }
                        : null,
                }),
            Attention = attention,
            Deadlines = contested
                .Where(e => e.OpportunityStatus == OpportunityStatus.Open && e.DeadlineUtc != null)
                .OrderBy(e => e.DeadlineUtc)
                .Take(5)
                .Select(e => new FreelancerDeadline
                {
                    Slug = e.Slug, Title = e.Title, DeadlineUtc = e.DeadlineUtc,
                    Claimed = e.Claimed, Possible = e.Milestones,
                }),
            Recent = recent.Select(r => new RecentEvent
            {
                AtUtc = r.ClaimedAtUtc,
                Text = $"Claimed milestone {r.Order + 1} — {r.Milestone}",
                Detail = $"{(r.Via == "tag" ? "tag" : "pull request")} {r.Ref}",
                Href = $"/opportunities/{r.Slug}",
            }),
        };
    }

    // =====================================================================
    // Admin — portal health, plus both sides of the marketplace
    // =====================================================================

    /// <summary>One open opportunity's applications still under review, and when the longest-waiting was filed.</summary>
    private sealed record WaitingApplications(string Slug, string Title, int Count, DateTimeOffset Oldest);

    /// <summary>
    /// Applications still to decide, per opportunity — one client's, or every
    /// client's for an administrator. Only an open opportunity's can be
    /// decided, so one that has moved on stops counting; the opportunity with
    /// the longest-waiting applicant comes first.
    /// </summary>
    private static async Task<List<WaitingApplications>> WaitingApplicationsAsync(
        AppDbContext db, Guid? clientId, CancellationToken ct)
    {
        var rows = db.UseDapper ? await WaitingSqlAsync(db.Sql, clientId, ct) : await WaitingLinqAsync(db, clientId, ct);
        return rows
            .GroupBy(a => new { a.OpportunityId, a.Slug, a.Title })
            .Select(g => new WaitingApplications(g.Key.Slug, g.Key.Title, g.Count(), g.Min(a => a.SubmittedAtUtc)))
            .OrderBy(w => w.Oldest)
            .ToList();
    }

    private static async Task<AdminDashboard> AdminAsync(
        AppDbContext db, DateTimeOffset now, DateOnly from, DateTimeOffset since, TimeZoneInfo zone, CancellationToken ct)
    {
        // The twelve reads, in the LINQ or the T-SQL.
        var r = db.UseDapper
            ? await AdminReadsSqlAsync(db.Sql, now, since, ct)
            : await AdminReadsLinqAsync(db, now, since, ct);
        var usersByRole = r.UsersByRole.ToDictionary(x => x.Role, x => x.Count, StringComparer.Ordinal);
        var signups = r.Signups;
        var opportunitiesByStatus = r.OpportunitiesByStatus.ToDictionary(x => x.Status, x => x.Count);
        var publishStamps = r.PublishStamps;
        var entryStamps = r.EntryStamps;
        var claimStamps = r.ClaimStamps;
        var provisionByStatus = r.ProvisionByStatus.ToDictionary(x => x.Status, x => x.Count);
        var awards = r.Awards;
        var deliveries = r.Deliveries;
        var failedRepos = r.FailedRepos;
        var clientRows = r.TopClients;
        var freelancerRows = r.TopFreelancers;

        // Applications still to decide, portal-wide: the clients' decisions,
        // which an administrator may also make.
        var waiting = await WaitingApplicationsAsync(db, null, ct);

        // ---- what needs the operator's attention -------------------------
        var attention = new List<AttentionItem>();

        foreach (var f in failedRepos)
            attention.Add(Item("repo_failed", "danger",
                $"Repo provisioning failed for @{f.GithubUsername}",
                $"“{f.Title}” · {f.ProvisionAttempts} attempt{(f.ProvisionAttempts == 1 ? "" : "s")} · "
                + (f.ProvisionNote ?? "no reason recorded"),
                "/admin/operations#repositories"));

        // Two different failures wear the same "not verified" badge, and they
        // need different responses: a transfer that fired and is waiting on the
        // recipient, versus one that never fired at all. Reporting the second
        // as a pending transfer sends the operator chasing a handover that was
        // never requested.
        foreach (var a in awards.Where(a => a.PaidAtUtc is not null && a.Handover == HandoverStatus.Requested))
            attention.Add(Item("handover", "warn",
                $"Handover unverified on “{a.Title}”",
                $"Paid {Ago(now, a.PaidAtUtc!.Value)} and transferred to @{a.TransferTargetLogin}, "
                + "but a re-read has not seen the new owner yet."
                + (a.HandoverNote is null ? "" : $" {a.HandoverNote}"),
                "/admin/operations#handovers"));

        foreach (var a in awards.Where(a => a.PaidAtUtc is not null && a.Handover == HandoverStatus.NotStarted))
            attention.Add(Item("handover_none", "danger",
                $"Nothing was handed over on “{a.Title}”",
                $"{a.Client} paid {Ago(now, a.PaidAtUtc!.Value)} but no transfer was requested — "
                + (a.HandoverNote ?? "no reason recorded")
                + " The winner's code is still owned by the portal.",
                "/admin/operations#handovers"));

        foreach (var a in awards.Where(a => a.PaidAtUtc is null && a.AnnouncedAtUtc <= now.AddDays(-7)))
            attention.Add(Item("award_unpaid", "warn",
                $"Award unpaid for {Ago(now, a.AnnouncedAtUtc)} on “{a.Title}”",
                $"{a.Client} announced {a.Winner} as winner. Nothing transfers until the payment is confirmed.",
                $"/opportunities/{a.Slug}"));

        var unmatched = deliveries.Count(d =>
            d.HandledNote != null && d.HandledNote.Contains("no matching", StringComparison.OrdinalIgnoreCase));
        if (unmatched > 0)
            attention.Add(Item("webhooks", "info",
                $"{unmatched} webhook deliver{(unmatched == 1 ? "y" : "ies")} matched no entry",
                "Usually a repository outside the portal, or one whose entry was withdrawn. A persistent count means a rename went unnoticed.",
                "/admin/operations#deliveries"));

        return new AdminDashboard
        {
            Role = "admin",
            GeneratedAtUtc = now,
            Headline = new AdminHeadline
            {
                Users = usersByRole.Values.Sum(),
                Clients = usersByRole.GetValueOrDefault(Roles.Client),
                Freelancers = usersByRole.GetValueOrDefault(Roles.Freelancer),
                NewUsers = signups.Count,
                Opportunities = opportunitiesByStatus.Values.Sum(),
                OpenOpportunities = opportunitiesByStatus.GetValueOrDefault(OpportunityStatus.Open),
                InReview = opportunitiesByStatus.GetValueOrDefault(OpportunityStatus.Reviewing),
                Entries = provisionByStatus.Values.Sum(),
                Repos = provisionByStatus.GetValueOrDefault(RepoProvisionStatus.Provisioned),
                ReposFailed = provisionByStatus.GetValueOrDefault(RepoProvisionStatus.Failed),
                Webhooks24h = deliveries.Count,
                UnmatchedWebhooks = unmatched,
                StuckHandovers = awards.Count(a => a.PaidAtUtc is not null && a.Handover != HandoverStatus.Verified),
                ApplicationsWaiting = waiting.Sum(w => w.Count),
            },
            // Every open opportunity with applications under review, the
            // longest-waiting first, so the tile can say how long that is.
            Waiting = waiting.Select(w => new WaitingReview
            {
                Slug = w.Slug,
                Title = w.Title,
                Count = w.Count,
                OldestUtc = w.Oldest,
            }),
            Money = Money(awards.Select(a => (a.Currency, a.Amount, a.PaidAtUtc))),
            Activity = new DailyChart
            {
                Labels = Labels(from, WindowDays),
                Series = new ChartSeries[]
                {
                    new ChartSeries { Name = "Opportunities published", Points = Bucket(publishStamps, from, WindowDays, zone) },
                    new ChartSeries { Name = "Entries", Points = Bucket(entryStamps, from, WindowDays, zone) },
                    new ChartSeries { Name = "Milestones claimed", Points = Bucket(claimStamps, from, WindowDays, zone) },
                },
            },
            Signups = new DailyChart
            {
                Labels = Labels(from, WindowDays),
                Series = new ChartSeries[]
                {
                    new ChartSeries
                    {
                        Name = "Clients",
                        Points = Bucket(signups.Where(s => s.Role == Roles.Client).Select(s => s.CreatedAtUtc), from, WindowDays, zone),
                    },
                    new ChartSeries
                    {
                        Name = "Freelancers",
                        Points = Bucket(signups.Where(s => s.Role == Roles.Freelancer).Select(s => s.CreatedAtUtc), from, WindowDays, zone),
                    },
                },
            },
            StatusSplit = Split(
                ("Open", opportunitiesByStatus.GetValueOrDefault(OpportunityStatus.Open)),
                ("In review", opportunitiesByStatus.GetValueOrDefault(OpportunityStatus.Reviewing)),
                ("Awarded", opportunitiesByStatus.GetValueOrDefault(OpportunityStatus.Awarded)),
                ("Draft", opportunitiesByStatus.GetValueOrDefault(OpportunityStatus.Draft)),
                ("Cancelled", opportunitiesByStatus.GetValueOrDefault(OpportunityStatus.Cancelled))),
            ProvisionSplit = Split(
                ("Provisioned", provisionByStatus.GetValueOrDefault(RepoProvisionStatus.Provisioned)),
                ("Pending", provisionByStatus.GetValueOrDefault(RepoProvisionStatus.Pending)),
                ("Failed", provisionByStatus.GetValueOrDefault(RepoProvisionStatus.Failed))),
            // The account ids let each row open its person in Users and its
            // figures in the reports, which filter by account.
            TopClients = clientRows.Select(c => new TopClient
            {
                Id = c.ClientId,
                Name = c.Name,
                Opportunities = c.Opportunities,
                Entrants = c.Entrants,
                AwardsAnnounced = awards.Count(a => a.ClientId == c.ClientId),
                AwardsPaid = awards.Count(a => a.ClientId == c.ClientId && a.PaidAtUtc is not null),
                PaidValue = awards.Where(a => a.ClientId == c.ClientId && a.PaidAtUtc is not null).Sum(a => a.Amount),
                Currency = awards.FirstOrDefault(a => a.ClientId == c.ClientId)?.Currency ?? "USD",
                LastPostedUtc = c.LastPosted,
            }),
            TopFreelancers = freelancerRows.Select(f => new TopFreelancer
            {
                Id = f.FreelancerId,
                Name = f.Name,
                Entries = f.Entries,
                Claims = f.Claims,
                Pushes = f.Pushes,
                Wins = awards.Count(a => a.WinnerId == f.FreelancerId),
                Earned = awards.Where(a => a.WinnerId == f.FreelancerId && a.PaidAtUtc is not null).Sum(a => a.Amount),
                Currency = awards.FirstOrDefault(a => a.WinnerId == f.FreelancerId)?.Currency ?? "USD",
            }),
            Attention = attention,
            Recent = deliveries
                .OrderByDescending(d => d.ReceivedAtUtc)
                .Take(8)
                .Select(d => new RecentEvent
                {
                    AtUtc = d.ReceivedAtUtc,
                    Text = $"{d.Event} · {d.RepoFullName ?? "no repository"}",
                    Detail = d.HandledNote ?? "received",
                    // A delivery has no page of its own; the console lists it, and can replay it.
                    Href = "/admin/operations#deliveries",
                }),
        };
    }

    // =====================================================================
    // Shared shaping
    // =====================================================================

    private static AttentionItem Item(string kind, string severity, string title, string detail, string? href) =>
        new AttentionItem { Kind = kind, Severity = severity, Title = title, Detail = detail, Href = href };

    /// <summary>ISO day labels for the window — the UI decides how to render them.</summary>
    private static string[] Labels(DateOnly from, int days) =>
        Enumerable.Range(0, days).Select(i => from.AddDays(i).ToString("yyyy-MM-dd")).ToArray();

    /// <summary>
    /// The viewer's time zone by the IANA name a browser reports ("Asia/Karachi");
    /// UTC for none, or for a name this server cannot find.
    /// </summary>
    internal static TimeZoneInfo ZoneOf(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64
        && TimeZoneInfo.TryFindSystemTimeZoneById(name, out var zone)
            ? zone
            : TimeZoneInfo.Utc;

    /// <summary>
    /// The instant a day begins in a zone — the window's first moment — at offset
    /// zero, the only offset Npgsql will write to a timestamptz.
    /// </summary>
    internal static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).ToUniversalTime();
    }

    /// <summary>
    /// Counts stamps into one bucket per day, the day as it was in the viewer's
    /// zone. Done here rather than in SQL because the window is bounded and the
    /// projection is a single column — one round trip beats a date_trunc GROUP
    /// BY per series, and the buckets stay aligned with <see cref="Labels"/> by
    /// construction.
    /// </summary>
    internal static int[] Bucket(IEnumerable<DateTimeOffset> stamps, DateOnly from, int days, TimeZoneInfo zone)
    {
        var buckets = new int[days];
        foreach (var stamp in stamps)
        {
            var index = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(stamp, zone).DateTime).DayNumber - from.DayNumber;
            if (index >= 0 && index < days) buckets[index]++;
        }
        return buckets;
    }

    private static SplitChart Split(params (string Label, int Value)[] parts) => new SplitChart
    {
        Labels = parts.Select(p => p.Label).ToArray(),
        Values = parts.Select(p => p.Value).ToArray(),
    };

    /// <summary>
    /// Award totals per currency. Opportunities carry their own currency, so one
    /// summed number would quietly add rupees to dollars.
    /// </summary>
    private static MoneyTotals[] Money(IEnumerable<(string Currency, decimal Amount, DateTimeOffset? PaidAtUtc)> awards) =>
        awards.GroupBy(a => a.Currency, StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(a => a.Amount))
            .Select(g => new MoneyTotals
            {
                Currency = g.Key,
                Announced = g.Sum(a => a.Amount),
                Paid = g.Where(a => a.PaidAtUtc is not null).Sum(a => a.Amount),
                Outstanding = g.Where(a => a.PaidAtUtc is null).Sum(a => a.Amount),
                Count = g.Count(),
            })
            .ToArray();

    /// <summary>"3 days ago" / "in 3 days" — the attention lists read as sentences.</summary>
    private static string Ago(DateTimeOffset now, DateTimeOffset then, bool future = false)
    {
        var span = future ? then - now : now - then;
        if (span < TimeSpan.Zero) return future ? "now" : "just now";
        if (span.TotalHours < 1) return future ? "within the hour" : "just now";

        var prefix = future ? "in " : "";
        var suffix = future ? "" : " ago";
        if (span.TotalHours < 48)
        {
            var hours = (int)span.TotalHours;
            return $"{prefix}{hours} hour{(hours == 1 ? "" : "s")}{suffix}";
        }

        return $"{prefix}{(int)span.TotalDays} days{suffix}";
    }
}
