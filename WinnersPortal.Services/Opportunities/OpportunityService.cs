using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Notifications;
using WinnersPortal.Services.Profiles;
using WinnersPortal.Services.Identity;
using WinnersPortal.Services.Settings;
using WinnersPortal.Services.Storage;

namespace WinnersPortal.Services.Opportunities;

public sealed class OpportunityService(AppDbContext db, AiOptions ai, AiWorkSignal aiSignal, SettingsService settings, GitHubService github, StorageService storage, ActivityNote activity, EmailWorkSignal emailSignal, PushWorkSignal pushSignal, UnsubscribeTokens unsubscribe, IdentityOptions identity, PublicReads publicReads)
{
    /// <summary>One page of the feed as the database gave it, before any caller's side of it is added.</summary>
    private sealed record FeedPage(List<OpportunityCards.Row> Rows, bool HasMore);

    /// <summary>The figures' raw material: what each opportunity open for entry awards, and when entry closes.</summary>
    private sealed record OpenAward(decimal Award, string Currency, DateTimeOffset? Closes);

    public async Task<Outcome<OpportunityFeedResponse>> FeedAsync(string? status, string? q, string? category, string? subcategory, string? sort, bool? fit, string? cursor, int? limit, ClaimsPrincipal principal, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 20, 1, 50);
        var now = DateTimeOffset.UtcNow;
        // "Ending" is the other order the page offers: soonest deadline
        // first, and only opportunities still running — a list of what is
        // about to end has no room for what ended in March.
        var ending = string.Equals(sort, "ending", StringComparison.OrdinalIgnoreCase);
        // "Award" is the third order: the largest award first, newest
        // among equals — for the member reading by what the work pays.
        var byAward = string.Equals(sort, "award", StringComparison.OrdinalIgnoreCase);

        // What is asked for, decided once: "Open for entry" has to mean
        // enterable, or the filter is a lie; words go through the
        // database's own text search and the title as typed; an unknown
        // category key matches nothing rather than everything; the cursor
        // is the keyset on whichever order is in force, and one without an
        // award under that order is somebody else's, and restarts the list.
        var catKey = OpportunityCategories.CleanKey(category);
        var feed = new OpportunityCards.FeedQuery(
            OpenOnly: status is null or "open",
            Q: string.IsNullOrWhiteSpace(q) ? null : q,
            CategoryKey: catKey,
            SubcategoryKey: catKey is null ? null : OpportunityCategories.CleanKey(subcategory),
            Ending: ending,
            ByAward: byAward,
            Cursor: Cursor.Decode(cursor) is { } cur && (!byAward || cur.Amount is not null) ? cur : null,
            Take: take + 1,
            Now: now);

        // The card's row and its wire shape live in OpportunityCards, shared
        // with the welcome screen's single card — and so does the read,
        // in the LINQ or the T-SQL. The rows are the same for everybody
        // who asks the same thing, so they are held for a few seconds
        // (PublicReads) under what was asked, less the clock it was asked at.
        var page = await publicReads.GetAsync(("opportunities.feed", feed with { Now = default }), db,
            async (reader, token) =>
            {
                var rows = reader.UseDapper
                    ? await OpportunityCards.FeedSqlAsync(reader.Sql, feed, token)
                    : await OpportunityCards.FeedLinqAsync(reader, feed, token);
                var more = rows.Count > take;
                if (more) rows.RemoveAt(take);
                await OpportunityCards.CountApplicationsAsync(reader, rows, token);
                return new FeedPage(rows, more);
            }, ct);
        var rows = page.Rows;
        var hasMore = page.HasMore;
        var last = rows.Count > 0 ? rows[^1] : null;

        // The viewer's own side of the fit, asked for by the Recommended
        // switch and read once for the page — never held, and laid over
        // the shared rows card by card below. The feed is public and
        // stays so: without the switch, or for anyone but a freelancer,
        // no card carries a verdict.
        var viewer = fit == true
            ? await FitReader.ForAsync(db, Principal.UserId(principal), Principal.Role(principal), ai, aiSignal, ct)
            : null;

        return Outcome.Ok(new OpportunityFeedResponse
        {
            Items = rows.Select(c => OpportunityCards.Dto(c, viewer, now)),
            NextCursor = hasMore && last != null
                ? new Cursor(
                    ending ? last.DeadlineUtc!.Value : last.PublishedAtUtc!.Value,
                    last.Id,
                    byAward ? last.AwardAmount : null).Encode()
                : null,
        });
    }

    public async Task<Outcome<OpportunityStatsResponse>> StatsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // Held for a few seconds like the feed's rows (PublicReads), and
        // cut again by this request's clock, so an opportunity whose entry
        // closed since the read has already left the count.
        var open = await publicReads.GetAsync("opportunities.stats", db,
            (reader, token) => reader.Opportunities.AsNoTracking()
                .Where(c => c.Status == OpportunityStatus.Open && (c.EntryCloseUtc ?? c.DeadlineUtc) > now)
                .Select(c => new OpenAward(c.AwardAmount, c.Currency, c.EntryCloseUtc ?? c.DeadlineUtc))
                .ToListAsync(token), ct);
        var f = OpportunityStats.Of(open.Where(r => r.Closes > now).Select(r => (r.Award, r.Currency, r.Closes)), now);
        return Outcome.Ok(new OpportunityStatsResponse { Open = f.Open, Rewards = f.Rewards, Currency = f.Currency, EndingSoon = f.EndingSoon });
    }

    public async Task<Outcome<IEnumerable<MyOpportunityRow>>> MineAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var clientId = Principal.UserId(principal)!.Value;
        var rows = await db.Opportunities.AsNoTracking()
            .Where(c => c.ClientId == clientId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => new
            {
                c.Id, c.Slug, c.Title, c.AwardAmount, c.Currency,
                c.DeadlineUtc, c.StartsAtUtc, c.PublishedAtUtc, c.Status, c.CreatedAtUtc,
                EntrantCount = c.ActiveEntryCount,
                MilestoneCount = c.MilestoneCount,
            })
            .ToListAsync(ct);
        // Applications still to decide, per open opportunity — only an open
        // opportunity's can be decided; the dashboard's tile adds these up.
        var waiting = await db.Applications.AsNoTracking()
            .Where(a => a.Opportunity!.ClientId == clientId
                && a.Opportunity.Status == OpportunityStatus.Open
                && a.Status == ApplicationStatus.UnderReview)
            .GroupBy(a => a.OpportunityId)
            .Select(g => new { OpportunityId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OpportunityId, x => x.Count, ct);
        return Outcome.Ok(rows.Select(c => new MyOpportunityRow
        {
            Id = c.Id,
            Slug = c.Slug,
            Title = c.Title,
            AwardAmount = c.AwardAmount,
            Currency = c.Currency,
            DeadlineUtc = c.DeadlineUtc,
            // The card's timeline counts from these.
            StartsAtUtc = c.StartsAtUtc,
            PublishedAtUtc = c.PublishedAtUtc,
            Status = OpportunityNames.StatusName(c.Status),
            CreatedAtUtc = c.CreatedAtUtc,
            EntrantCount = c.EntrantCount,
            MilestoneCount = c.MilestoneCount,
            ApplicationsWaiting = waiting.GetValueOrDefault(c.Id),
        }));
    }

    public async Task<Outcome<OpportunityDetail>> DetailAsync(string slug, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal);
        var c = await db.Opportunities.AsNoTracking()
            .Include(x => x.Client)
            .Include(x => x.Milestones.OrderBy(m => m.Order))
            .Include(x => x.Skills.OrderBy(s => s.Order))
            .Include(x => x.Requirements.OrderBy(r => r.Order))
            .Include(x => x.Criteria.OrderBy(r => r.Order))
            .SingleOrDefaultAsync(x => x.Slug == slug, ct);
        if (c is null) return Outcome.NotFound();
        var categoryDef = OpportunityCategories.Find(c.Category);
        var subcategoryDef = categoryDef is null ? null : OpportunityCategories.FindSub(categoryDef, c.Subcategory);
        var requiredSkills = c.Skills.OrderBy(s => s.Order).Select(s => s.Name).ToList();

        // Drafts are visible only to their author (and the admin console later).
        var isOwner = viewerId == c.ClientId;
        // An administrator moderates every opportunity but owns none: they
        // may take an entrant off, and nothing else the owner can do.
        var isAdmin = principal.IsInRole(Roles.Admin);
        var canModerate = isOwner || isAdmin;
        if (c.Status == OpportunityStatus.Draft && !isOwner) return Outcome.NotFound();

        // A published opportunity reads the same for everybody: the brief, its
        // terms, the board and the client's record are the page, and a
        // visitor who cannot judge them cannot decide whether to join. What
        // stays behind the sign-in is what a session is actually needed
        // for — entering, and the bytes of the brief's attached files.

        // The viewer's fit — a freelancer's own answer to "can I enter,
        // and is it my kind of work". Null for everyone else.
        var viewer = await FitReader.ForAsync(db, viewerId, Principal.Role(principal), ai, aiSignal, ct);
        var viewerFit = viewer?.Judge(
            c.MinMeritScore, requiredSkills, c.Category,
            Schedule.StartsAt(c.StartsAtUtc, c.PublishedAtUtc), c.DeadlineUtc);

        // The checkpoint board — one row per entrant, one column per
        // milestone. Public by default (progress pressure is part of the
        // product); an operator can restrict it to the opportunity owner.
        var publicBoard = string.Equals(
            await settings.GetAsync("features.publicMilestoneBoard", ct), "true",
            StringComparison.OrdinalIgnoreCase);
        // An administrator moderating from the board needs the board.
        var showBoard = publicBoard || canModerate;
        _ = int.TryParse(await settings.GetAsync("limits.maxEntriesPerOpportunity", ct), out var maxEntries);

        // The public entrant list — deliberate, from the open-entry decision:
        // the list and the count are how entrants judge their own odds.
        var entrantRows = await db.Entries.AsNoTracking()
            .Where(e => e.OpportunityId == c.Id && e.Status == EntryStatus.Active)
            .OrderBy(e => e.CreatedAtUtc)
            .Select(e => new
            {
                e.Id,
                e.FreelancerId,
                DisplayName = e.Freelancer!.DisplayName,
                AvatarAt = e.Freelancer.AvatarUpdatedAtUtc,
                e.Note,
                e.CreatedAtUtc,
                e.LastPushAtUtc,
                e.RepoFullName,
                e.ProvisionStatus,
                e.FrozenAtUtc,
                // The upload half of delivery: confirmed files only.
                Files = e.Submissions
                    .Where(s => s.UploadedAtUtc != null)
                    .OrderBy(s => s.UploadedAtUtc)
                    .Select(s => new
                    {
                        s.Id, s.FileName, s.ContentType, s.SizeBytes, s.UploadedAtUtc,
                        MilestoneOrder = s.Milestone != null ? s.Milestone.Order : (int?)null,
                    })
                    .ToList(),
                Claimed = e.Checkpoints
                    .OrderBy(cp => cp.Milestone!.Order)
                    .Select(cp => new { cp.Milestone!.Order, cp.ClaimedAtUtc, cp.Id, cp.BuildStatus, cp.BuildFinishedAtUtc })
                    .ToList(),
                // Each entrant's portable record: opportunities won anywhere on
                // the portal, and what clients said about working with them.
                Wins = db.Awards.Count(a => a.Entry!.FreelancerId == e.FreelancerId),
                RatingCount = db.Ratings.Count(r => r.OfUserId == e.FreelancerId),
                RatingAvg = db.Ratings.Where(r => r.OfUserId == e.FreelancerId)
                    .Average(r => (double?)r.Stars),
            })
            .ToListAsync(ct);

        // The previews up or on their way, where the opportunity runs them: one
        // read for the page, keyed by the preview's id (a checkpoint's for a
        // milestone, an entry's for the final).
        var entryIds = entrantRows.Select(e => e.Id).ToList();
        var previewStates = c.RequiresCompose && entryIds.Count > 0
            ? await db.Previews.AsNoTracking()
                .Where(p => entryIds.Contains(p.EntryId) && !p.StopRequested
                    && (p.Status == PreviewStatus.Pending || p.Status == PreviewStatus.Starting || p.Status == PreviewStatus.Running))
                .Select(p => new { p.Id, p.Status })
                .ToDictionaryAsync(p => p.Id, p => p.Status, ct)
            : new Dictionary<Guid, PreviewStatus>();
        string? PreviewOf(Guid id) => previewStates.TryGetValue(id, out var s) ? Preview.Previews.StatusName(s) : null;
        // An entry's final, where it can run at all: "none" until started,
        // null before the freeze, after a cancellation, or where the opportunity
        // runs nothing — the same rule the start is judged by.
        string? FinalOf(Guid entryId, DateTimeOffset? frozenAt) =>
            Preview.Previews.FinalProblem(c.Status, c.RequiresCompose, frozenAt != null, EntryStatus.Active) is null
                ? PreviewOf(entryId) ?? "none"
                : null;

        // Each entrant's merit score, batched for the whole list rather
        // than computed per row — four reads whatever the entrant count.
        var merit = await MeritReader.MeritForAsync(
            db, entrantRows.Select(e => e.FreelancerId).ToList(), ct);

        // For the client alone: each entrant's past work that fits this
        // brief and what came of it, picked as the welcome screen picks a
        // member's own. Only work they have said may be shown publicly,
        // because the client is somebody else; read through the profile,
        // because the project table has no filter of its own for a
        // closed profile.
        var fittingWork = new Dictionary<Guid, PastWork>();
        if (isOwner && entrantRows.Count > 0)
        {
            var workOwners = entrantRows.Select(e => e.FreelancerId).ToList();
            var shownWork = await db.Profiles.AsNoTracking()
                .Where(p => workOwners.Contains(p.UserId))
                .SelectMany(p => p.Projects
                    .Where(x => x.MayShowPublicly && x.Outcome != null)
                    .Select(x => new { x.UserId, x.Order, x.Title, x.Category, x.Description, x.Outcome, x.Role, x.Tech }))
                .ToListAsync(ct);
            foreach (var byOwner in shownWork.GroupBy(x => x.UserId))
                if (Assessment.FittingWork(
                        byOwner.OrderBy(x => x.Order).Select(x => new PastWork(
                            x.Title, x.Outcome,
                            ProjectFacts.Of(x.Category, x.Title, x.Description, x.Outcome, x.Role, x.Tech))),
                        requiredSkills, c.Category) is { } picked)
                    fittingWork[byOwner.Key] = picked;
        }

        // The board read against the milestone dates: one state per
        // column, and the row's standing summarised. Ordered milestones,
        // so column i is milestone i everywhere the board is drawn.
        var now = DateTimeOffset.UtcNow;
        var ordered = c.Milestones.OrderBy(m => m.Order).ToList();

        // Every entrant's standing on this opportunity — the number the board
        // is ordered by and the reading beside the remove button. One
        // batched read; the arithmetic is Standing's, line by line.
        var standings = await StandingReader.ForOpportunitiesAsync(db, [c.Id], now, ct);
        var entrants = entrantRows
            .Select(e =>
            {
                var states = ordered
                    .Select(m => Schedule.StateOf(
                        m.DueUtc,
                        e.Claimed.FirstOrDefault(cp => cp.Order == m.Order)?.ClaimedAtUtc,
                        now))
                    .ToList();
                return (Row: e, States: states, Standing: Schedule.Stand(states));
            })
            // Best standing first — the score that reads this board, the
            // record elsewhere and the portfolio together — and join
            // order for whatever it cannot tell apart, so a board with
            // nothing readable yet reads exactly as it always did. The
            // on-time counts below stay: they are the biggest part of the
            // score, and what a person should read.
            .OrderBy(x => standings.TryGetValue(x.Row.Id, out var s) ? s.Rank : int.MaxValue)
            .ThenBy(x => x.Row.CreatedAtUtc)
            .Select(x =>
            {
                var (e, states, standing) = x;
                merit.TryGetValue(e.FreelancerId, out var m);
                standings.TryGetValue(e.Id, out var st);
                var own = e.FreelancerId == viewerId;
                return new OpportunityEntrant
                {
                    DisplayName = e.DisplayName,
                    AvatarUrl = AvatarRules.Url(e.FreelancerId, e.AvatarAt),
                    // The portfolio they keep, alongside the record the
                    // portal kept for them. The link is where the score's
                    // arithmetic is on show.
                    UserId = e.FreelancerId,
                    Headline = m.Headline,
                    // The client's alone, and null without a fitting
                    // public project that says what came of it.
                    PastWork = fittingWork.TryGetValue(e.FreelancerId, out var fitting)
                        ? new WorkHighlight { Title = fitting.Title, Outcome = fitting.Outcome }
                        : null,
                    MeritScore = m.Score,
                    MeritBand = m.Band ?? "new here",
                    Note = e.Note,
                    EnteredAtUtc = e.CreatedAtUtc,
                    Wins = e.Wins,
                    RatingAvg = e.RatingAvg,
                    RatingCount = e.RatingCount,
                    // board columns — 1-based milestone numbers, matching the tags
                    MilestonesDone = showBoard ? e.Claimed.Select(cp => cp.Order + 1) : null,
                    // …and the same columns told apart by their dates.
                    MilestoneStates = showBoard ? states.Select(Schedule.Name).ToList() : null,
                    Standing = showBoard ? new MilestoneTally
                    {
                        Done = standing.Done,
                        OnTime = standing.OnTime,
                        Late = standing.Late,
                        Overdue = standing.Overdue,
                        Dated = standing.Dated,
                    } : null,
                    // The standing score: where this row sits out of 100,
                    // its place, and the one-word reading. As public as
                    // the board; the arithmetic behind it opens for the
                    // entrant themselves and for whoever can remove them.
                    StandingScore = showBoard && st is not null ? st.Score : (int?)null,
                    StandingBand = showBoard && st is not null ? Standing.BandName(st.Band) : null,
                    StandingRank = showBoard && st is not null ? st.Rank : (int?)null,
                    StandingParts = st is not null && (canModerate || own)
                        ? st.Parts.Select(p => new StandingPartView
                        {
                            Key = p.Key, Label = p.Label, Earned = p.Earned, Available = p.Available, Detail = p.Detail,
                        }).ToList()
                        : null,
                    LastPushAtUtc = showBoard ? e.LastPushAtUtc : null,
                    // How much was handed in is as public as the board;
                    // the files themselves open on the delivery rule —
                    // the entrant's own always, the client's from the
                    // deadline, an administrator's at any time.
                    FilesUploaded = showBoard ? e.Files.Count : (int?)null,
                    Files = Delivery.CanSeeFiles(e.FreelancerId == viewerId, isOwner, isAdmin, c.Status)
                        ? e.Files.Select(f => SubmissionService.Summary(
                            f.Id, f.FileName, f.ContentType, f.SizeBytes, f.UploadedAtUtc, f.MilestoneOrder + 1))
                            .ToList()
                        : null,
                    // review controls — the owner announces from these
                    // rows, and an administrator moderates from them
                    EntryId = canModerate ? e.Id : (Guid?)null,
                    RepoFullName = isOwner ? e.RepoFullName : null,
                    // The build of each claim, one cell per milestone — only
                    // where the opportunity asks for builds at all.
                    Builds = showBoard && c.RequiresCompose
                        ? ordered.Select(m =>
                        {
                            var cp = e.Claimed.FirstOrDefault(x => x.Order == m.Order);
                            return cp is null || cp.BuildStatus == PreviewBuildStatus.None
                                ? new BuildCellView("none", null, null)
                                : new BuildCellView(Preview.CheckpointBuilds.StatusName(cp.BuildStatus), cp.Id, cp.BuildFinishedAtUtc,
                                    PreviewOf(cp.Id));
                        }).ToList()
                        : null,
                    // The final version's preview, for those who may run it.
                    FinalPreview = canModerate || own ? FinalOf(e.Id, e.FrozenAtUtc) : null,
                };
            })
            .ToList();

        var awardRow = await db.Awards.AsNoTracking()
            .Where(a => a.OpportunityId == c.Id)
            .Select(a => new
            {
                a.Id, a.EntryId, a.AnnouncedAtUtc, a.PaidAtUtc, a.Handover,
                a.TransferTargetLogin, a.TransferRequestedAtUtc, a.HandoverVerifiedAtUtc, a.HandoverNote,
                WinnerName = a.Entry!.Freelancer!.DisplayName,
                WinnerUserId = a.Entry.FreelancerId,
                WinnerRepo = a.Entry.RepoFullName,
            })
            .SingleOrDefaultAsync(ct);

        // Both directions of this award's ratings, public once written —
        // the winner's score of the client and the client's of the winner.
        var awardRatings = awardRow is null
            ? []
            : await db.Ratings.AsNoTracking()
                .Where(r => r.AwardId == awardRow.Id)
                .Select(r => new { r.ByUserId, r.OfUserId, r.Stars, r.Comment, r.UpdatedAtUtc })
                .ToListAsync(ct);
        var ratingOfClient = awardRatings.FirstOrDefault(r => r.OfUserId == c.ClientId);
        var ratingOfWinner = awardRatings.FirstOrDefault(r => r.OfUserId != c.ClientId);
        var viewerIsWinner = viewerId is not null && viewerId == awardRow?.WinnerUserId;

        // The client's public payment record — the counterweight to "no
        // deposit, open entry". Entrants stake real work on this client's
        // promise; here is how their past promises went.
        var clientAwardRows = await db.Awards.AsNoTracking()
            .Where(a => a.Opportunity!.ClientId == c.ClientId)
            .Select(a => new { a.AnnouncedAtUtc, a.PaidAtUtc })
            .ToListAsync(ct);
        var daysToPay = clientAwardRows
            .Where(a => a.PaidAtUtc != null)
            .Select(a => TrackRecordMath.DaysToPay(a.AnnouncedAtUtc, a.PaidAtUtc!.Value))
            .ToList();
        var oldestUnpaid = clientAwardRows
            .Where(a => a.PaidAtUtc == null)
            .Select(a => (DateTimeOffset.UtcNow - a.AnnouncedAtUtc).TotalDays)
            .OrderDescending()
            .Cast<double?>()
            .FirstOrDefault();
        // Everything ever published counts as posted — a cancellation must
        // not shrink the denominator it is judged against. The cancelled
        // count itself only includes opportunities that had entrants: calling
        // off an empty opportunity wasted nobody's staked work.
        var opportunitiesPosted = await db.Opportunities.CountAsync(x =>
            x.ClientId == c.ClientId && x.Status != OpportunityStatus.Draft, ct);
        var opportunitiesCancelled = await db.Opportunities.CountAsync(x =>
            x.ClientId == c.ClientId
            && x.Status == OpportunityStatus.Cancelled
            && x.Entries.Any(e => e.Status == EntryStatus.Active), ct);
        var clientRatings = await db.Ratings.AsNoTracking()
            .Where(r => r.OfUserId == c.ClientId)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Avg = g.Average(r => (double)r.Stars) })
            .SingleOrDefaultAsync(ct);
        var clientReviews = await db.Ratings.AsNoTracking()
            .Where(r => r.OfUserId == c.ClientId && r.Comment != null)
            .OrderByDescending(r => r.UpdatedAtUtc)
            .Take(3)
            .Select(r => new
            {
                r.Stars, r.Comment, r.UpdatedAtUtc,
                ByName = r.ByUser!.DisplayName,
                OpportunityTitle = r.Award!.Opportunity!.Title,
            })
            .ToListAsync(ct);

        // Brief attachments: metadata is as public as the brief itself;
        // the bytes stay behind the signed-in download and view links.
        // Listed as the draft editor lists them, previews included.
        var attachments = (await db.Attachments.AsNoTracking()
            .Where(a => a.OpportunityId == c.Id && a.UploadedAtUtc != null)
            .OrderBy(a => a.UploadedAtUtc)
            .Select(a => new { a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedAtUtc })
            .ToListAsync(ct))
            .Select(a => AttachmentService.Summary(
                a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedAtUtc))
            .ToList();

        // The day's AI-drafted board note. It shows exactly where the
        // board shows and vanishes with the master switch — turning AI
        // off hides every AI panel, this one included.
        var narrativeRow = showBoard && await ai.IsEnabledAsync(ct)
            ? await db.AiArtifacts.AsNoTracking()
                .Where(a => a.Feature == AiFeature.ProgressNarrative
                    && a.SubjectId == c.Id
                    && a.Status == AiArtifactStatus.Done
                    && a.OutputJson != null)
                .Select(a => new { a.OutputJson, a.CompletedAtUtc })
                .SingleOrDefaultAsync(ct)
            : null;
        string? narrativeText = null;
        if (narrativeRow is not null)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(narrativeRow.OutputJson!);
            if (doc.RootElement.TryGetProperty("narrative", out var n))
                narrativeText = n.GetString();
        }

        var viewerEntryRow = viewerId is null ? null : await db.Entries.AsNoTracking()
            .Where(e => e.OpportunityId == c.Id && e.FreelancerId == viewerId && e.Status == EntryStatus.Active)
            .Select(e => new
            {
                e.Id, e.GithubUsername, e.CreatedAtUtc, e.RepoFullName, e.ProvisionStatus,
                Claimed = e.Checkpoints.Select(cp => cp.Milestone!.Order).ToList(),
                Files = e.Submissions
                    .Where(s => s.UploadedAtUtc != null)
                    .OrderBy(s => s.UploadedAtUtc)
                    .Select(s => new
                    {
                        s.Id, s.FileName, s.ContentType, s.SizeBytes, s.UploadedAtUtc,
                        MilestoneOrder = s.Milestone != null ? s.Milestone.Order : (int?)null,
                    })
                    .ToList(),
            })
            .SingleOrDefaultAsync(ct);
        // Applications: the viewer's own, if any, so the page can say
        // where it stands instead of offering the wizard again; the
        // count, which is as public as the entrant count; and, for the
        // client and an administrator, every one to decide on.
        var viewerApplication = viewerId is null ? null : await db.Applications.AsNoTracking()
            .Where(a => a.OpportunityId == c.Id && a.FreelancerId == viewerId)
            .Select(a => new { a.Id, a.Status, a.SubmittedAtUtc, a.DecidedAtUtc })
            .SingleOrDefaultAsync(ct);
        var applicationCount = await db.Applications.CountAsync(a => a.OpportunityId == c.Id, ct);
        var applications = canModerate
            ? await ApplicationService.ReviewRowsAsync(
                db, c.Id, c.Category, c.Status, Schedule.StartsAt(c.StartsAtUtc, c.PublishedAtUtc), now, ct)
            : null;
        StandingReader.Row? viewerStanding = null;
        if (viewerEntryRow is not null) standings.TryGetValue(viewerEntryRow.Id, out viewerStanding);

        return Outcome.Ok(new OpportunityDetail
        {
            Id = c.Id,
            Slug = c.Slug,
            Title = c.Title,
            BriefMarkdown = c.BriefMarkdown,
            AwardAmount = c.AwardAmount,
            Currency = c.Currency,
            Status = OpportunityNames.StatusName(c.Status),
            // Repository, upload, or both — every role-specific panel on
            // the page branches on this before it says the word "repo".
            Delivery = Delivery.Name(c.Delivery),
            RequiresCompose = c.RequiresCompose,
            DeadlineUtc = c.DeadlineUtc,
            // The last joining date, already resolved: null on the row
            // means entry runs to the deadline, and no reader of this
            // payload should have to know that rule.
            EntryCloseUtc = Schedule.EntryClosesAt(c.EntryCloseUtc, c.DeadlineUtc),
            EntryOpen = Schedule.EntryOpen(c.Status, c.EntryCloseUtc, c.DeadlineUtc, now),
            // The client's own start day, raw: null means the moment it
            // was published, and the editor needs blank back as blank.
            // Information only — entry is open from publishing either way.
            StartsAtUtc = c.StartsAtUtc,
            // True only when the two dates actually differ — the page
            // says "entry closes earlier than the deadline" from this,
            // and must not say it about an opportunity that set no such date.
            EntryClosesEarly = c.EntryCloseUtc is not null && c.EntryCloseUtc != c.DeadlineUtc,
            PublishedAtUtc = c.PublishedAtUtc,
            // As public as the brief it voided — entrants get the same
            // reason the email carried, and so does everyone after them.
            CancelledAtUtc = c.CancelledAtUtc,
            CancelReason = c.CancelReason,
            ClientName = c.Client!.DisplayName,
            ClientAvatarUrl = AvatarRules.Url(c.ClientId, c.Client.AvatarUpdatedAtUtc),
            ClientRecord = new ClientRecord
            {
                OpportunitiesPosted = opportunitiesPosted,
                OpportunitiesCancelled = opportunitiesCancelled,
                AwardsAnnounced = clientAwardRows.Count,
                AwardsPaid = daysToPay.Count,
                MedianDaysToPay = TrackRecordMath.Median(daysToPay),
                OldestUnpaidDays = oldestUnpaid is null ? (int?)null : (int)Math.Floor(oldestUnpaid.Value),
                RatingAvg = clientRatings?.Avg,
                RatingCount = clientRatings?.Count ?? 0,
                Reviews = clientReviews.Select(r => new ClientReview
                {
                    Stars = r.Stars,
                    Comment = r.Comment,
                    ByName = r.ByName,
                    OpportunityTitle = r.OpportunityTitle,
                    AtUtc = r.UpdatedAtUtc,
                }),
            },
            Milestones = ordered.Select(m => new OpportunityMilestoneView
            {
                Title = m.Title, Description = m.Description, DueUtc = m.DueUtc, WeightPercent = m.WeightPercent,
            }),
            Attachments = attachments,
            // The terms beside the brief: what the work must be built
            // with, and what it is scored on. Members only, like the brief.
            Requirements = c.Requirements.OrderBy(r => r.Order)
                .Select(r => new OpportunityRequirementView { Title = r.Title, Detail = r.Detail }),
            Criteria = c.Criteria.OrderBy(r => r.Order)
                .Select(r => new OpportunityCriterionView { Points = r.Points, Title = r.Title, Description = r.Description }),
            // What kind of work, and who it is for — the terms the
            // entry door checks, as public as the award.
            Category = c.Category,
            CategoryLabel = categoryDef?.Label,
            Subcategory = c.Subcategory,
            SubcategoryLabel = subcategoryDef?.Label,
            MinMeritScore = c.MinMeritScore,
            Skills = requiredSkills,
            ViewerFit = viewerFit is null ? null : FitReader.Dto(viewerFit),
            // Search metadata: as public as the page head it renders into.
            MetaTitle = c.MetaTitle,
            MetaDescription = c.MetaDescription,
            // The AI-drafted board note, labelled as such wherever it renders.
            Narrative = narrativeText is null ? null : new OpportunityNarrative
            {
                Text = narrativeText,
                AtUtc = narrativeRow!.CompletedAtUtc,
            },
            Entrants = entrants,
            EntrantCount = entrants.Count,
            ApplicationCount = applicationCount,
            ViewerApplication = viewerApplication is null ? null : new ViewerApplication
            {
                Id = viewerApplication.Id,
                Status = ApplicationRules.StatusName(viewerApplication.Status),
                SubmittedAtUtc = viewerApplication.SubmittedAtUtc,
                DecidedAtUtc = viewerApplication.DecidedAtUtc,
            },
            // The client's (and an administrator's) review box; null for everyone else.
            Applications = applications,
            // limits.maxEntriesPerOpportunity, surfaced so the entry form can
            // say "full" up front instead of failing on submit. Null when
            // uncapped — the intended default.
            MaxEntries = maxEntries == 0 ? (int?)null : maxEntries,
            EntriesFull = maxEntries > 0 && entrants.Count >= maxEntries,
            ShowBoard = showBoard,
            IsOwner = isOwner,
            IsAdmin = isAdmin,
            // The connect requirement only exists where the integration
            // does — without it the UI must not lock announcing behind a
            // connect flow that cannot succeed. Owner-only detail.
            GithubConfigured = isOwner ? await github.IsConfiguredAsync(ct) : (bool?)null,
            // Same shape for storage: the ZIP fallback only renders
            // where a signed URL could actually be minted.
            StorageConfigured = isOwner ? await storage.IsConfiguredAsync(ct) : (bool?)null,
            ViewerEntry = viewerEntryRow is null ? null : new ViewerEntry
            {
                Id = viewerEntryRow.Id,
                GithubUsername = viewerEntryRow.GithubUsername,
                EnteredAtUtc = viewerEntryRow.CreatedAtUtc,
                RepoFullName = viewerEntryRow.RepoFullName,
                ProvisionStatus = OpportunityNames.ProvisionName(viewerEntryRow.ProvisionStatus),
                MilestonesDone = viewerEntryRow.Claimed.Select(o => o + 1).OrderBy(n => n),
                // The entrant's own files, and whether another may land.
                Files = viewerEntryRow.Files.Select(f => SubmissionService.Summary(
                    f.Id, f.FileName, f.ContentType, f.SizeBytes, f.UploadedAtUtc, f.MilestoneOrder + 1)),
                UploadProblem = Delivery.UsesUpload(c.Delivery)
                    ? Delivery.UploadProblem(c.Status, c.DeadlineUtc, now)
                    : "This opportunity is delivered through GitHub.",
                // Their own standing, with the arithmetic and the one
                // step that would move it most — the entrant's side of
                // the number the client reads.
                Standing = viewerStanding is null ? null : new ViewerStanding
                {
                    Score = viewerStanding.Score,
                    Band = Standing.BandName(viewerStanding.Band),
                    Rank = viewerStanding.Rank,
                    Of = viewerStanding.Of,
                    Parts = viewerStanding.Parts.Select(p => new StandingPartView
                    {
                        Key = p.Key, Label = p.Label, Earned = p.Earned, Available = p.Available, Detail = p.Detail,
                    }),
                    NextStep = viewerStanding.NextStep,
                },
                // Their own final, run as the client runs it. The entry is
                // active, so it is one of the rows read above.
                FinalPreview = FinalOf(viewerEntryRow.Id,
                    entrantRows.FirstOrDefault(e => e.Id == viewerEntryRow.Id)?.FrozenAtUtc),
            },
            Award = awardRow is null ? null : new OpportunityAward
            {
                // The announcement — and whether it was honoured — is
                // public: "an award left unpaid is visible to everyone
                // who looks" is a promise the winner's email makes.
                // The handover machinery below stays the owner's.
                WinnerName = awardRow.WinnerName,
                AnnouncedAtUtc = awardRow.AnnouncedAtUtc,
                PaidAtUtc = awardRow.PaidAtUtc,
                // Ratings are public the moment they exist; who may rate
                // and what the viewer already said are the viewer's.
                RatingOfClient = ratingOfClient is null ? null : new AwardRating
                {
                    Stars = ratingOfClient.Stars,
                    Comment = ratingOfClient.Comment,
                    ByName = awardRow.WinnerName,
                    UpdatedAtUtc = ratingOfClient.UpdatedAtUtc,
                },
                RatingOfWinner = ratingOfWinner is null ? null : new AwardRating
                {
                    Stars = ratingOfWinner.Stars,
                    Comment = ratingOfWinner.Comment,
                    ByName = c.Client!.DisplayName,
                    UpdatedAtUtc = ratingOfWinner.UpdatedAtUtc,
                },
                ViewerIsWinner = viewerIsWinner,
                CanRate = awardRow.PaidAtUtc != null && (isOwner || viewerIsWinner),
                MyRating = !(isOwner || viewerIsWinner)
                    ? null
                    : awardRatings.Where(r => r.ByUserId == viewerId)
                        .Select(r => new MyRating { Stars = r.Stars, Comment = r.Comment })
                        .FirstOrDefault(),
                Id = isOwner || viewerIsWinner ? awardRow.Id : (Guid?)null,
                Handover = isOwner ? AwardNames.HandoverName(awardRow.Handover) : null,
                WinnerEntryId = isOwner ? awardRow.EntryId : (Guid?)null,
                WinnerUserId = isOwner ? awardRow.WinnerUserId : (Guid?)null,
                TransferTargetLogin = isOwner ? awardRow.TransferTargetLogin : null,
                TransferRequestedAtUtc = isOwner ? awardRow.TransferRequestedAtUtc : null,
                HandoverVerifiedAtUtc = isOwner ? awardRow.HandoverVerifiedAtUtc : null,
                HandoverNote = isOwner ? awardRow.HandoverNote : null,
                WinnerRepoFullName = isOwner ? awardRow.WinnerRepo : null,
            },
        });
    }

    /// <param name="publishing">This save is the editor's Publish button on its way to publishing; it names the activity row, nothing else.</param>
    public async Task<Outcome<OpportunitySavedResponse>> CreateAsync(OpportunityUpsertRequest request, bool publishing, ClaimsPrincipal principal, CancellationToken ct)
    {
        activity.Action = ActivityNames.OpportunitySaved(isNew: true, publishing);
        var error = Validate(request);
        if (error is not null) return Outcome.Invalid(error);

        var opportunity = new Opportunity
        {
            Id = Guid.NewGuid(),
            Slug = await Slugs.UniqueAsync(db, request.Title!, null, ct),
            Title = request.Title!.Trim(),
            ClientId = Principal.UserId(principal)!.Value,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        activity.Subject = opportunity.Slug;
        Apply(opportunity, request);
        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync(ct);
        return Outcome.Ok(new OpportunitySavedResponse { Id = opportunity.Id, Slug = opportunity.Slug });
    }

    public async Task<Outcome<OpportunitySavedResponse>> UpdateAsync(Guid id, OpportunityUpsertRequest request, bool publishing, ClaimsPrincipal principal, CancellationToken ct)
    {
        activity.Action = ActivityNames.OpportunitySaved(isNew: false, publishing);
        var opportunity = await db.Opportunities.Include(c => c.Milestones).Include(c => c.Skills)
            .Include(c => c.Requirements).Include(c => c.Criteria)
            .SingleOrDefaultAsync(c => c.Id == id, ct);
        if (opportunity is null || opportunity.ClientId != Principal.UserId(principal)) return Outcome.NotFound();
        activity.Subject = opportunity.Slug;
        if (opportunity.Status != OpportunityStatus.Draft)
            return Outcome.Conflict("Only drafts can be edited. A published brief is what entrants committed their time to.");

        var error = Validate(request);
        if (error is not null) return Outcome.Invalid(error);
        db.OpportunitySkills.RemoveRange(opportunity.Skills);
        opportunity.Skills.Clear();
        db.OpportunityRequirements.RemoveRange(opportunity.Requirements);
        opportunity.Requirements.Clear();
        db.OpportunityCriteria.RemoveRange(opportunity.Criteria);
        opportunity.Criteria.Clear();

        if (!string.Equals(opportunity.Title, request.Title!.Trim(), StringComparison.Ordinal))
        {
            opportunity.Title = request.Title!.Trim();
            opportunity.Slug = await Slugs.UniqueAsync(db, opportunity.Title, opportunity.Id, ct);
            activity.Subject = opportunity.Slug;
        }
        db.Milestones.RemoveRange(opportunity.Milestones);
        opportunity.Milestones.Clear();
        Apply(opportunity, request);
        // Explicitly Added: reached by navigation from a tracked parent,
        // EF would guess these client-keyed rows are Modified — and then
        // update milestone rows that do not exist.
        db.Milestones.AddRange(opportunity.Milestones);
        db.OpportunitySkills.AddRange(opportunity.Skills);
        db.OpportunityRequirements.AddRange(opportunity.Requirements);
        db.OpportunityCriteria.AddRange(opportunity.Criteria);
        await db.SaveChangesAsync(ct);
        return Outcome.Ok(new OpportunitySavedResponse { Id = opportunity.Id, Slug = opportunity.Slug });
    }

    public async Task<Outcome<PublishResponse>> PublishAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var opportunity = await db.Opportunities.Include(c => c.Milestones).Include(c => c.Client)
            .SingleOrDefaultAsync(c => c.Id == id, ct);
        if (opportunity is null || opportunity.ClientId != Principal.UserId(principal)) return Outcome.NotFound();
        activity.Subject = opportunity.Slug;
        if (opportunity.Status != OpportunityStatus.Draft)
            return Outcome.Conflict("This opportunity is already published.");

        // The portal's door before the brief's own checks: a client sent
        // to verify comes back to a form that then says what else is
        // missing, not the other way round. The flag beside the sentence
        // is what the editor routes on.
        if (IdentityRules.PublishOwed(
                await identity.IsEnabledAsync(ct), await identity.RequiredForPublishAsync(ct),
                opportunity.Client!.IdentityVerifiedAtUtc is not null))
            return Outcome.Forbidden(new VerificationRequiredResponse
            {
                Error = IdentityRules.PublishProblem,
                VerificationRequired = true,
            });

        if (string.IsNullOrWhiteSpace(opportunity.BriefMarkdown))
            return Outcome.Invalid("Write the brief before publishing — it is what entrants build against.");
        if (opportunity.Milestones.Count == 0)
            return Outcome.Invalid("Add at least one milestone; the checklist becomes every entrant's board.");
        if (OpportunityCategories.PublishProblem(opportunity.Category) is { } uncategorised)
            return Outcome.Invalid(uncategorised);

        var minAward = decimal.TryParse(await settings.GetAsync("opportunity.minAwardUsd", ct), out var m) ? m : 0;
        if (opportunity.AwardAmount < minAward)
            return Outcome.Invalid($"The award must be at least {minAward:0} USD (opportunity policy).");

        var now = DateTimeOffset.UtcNow;
        // A start day that went by while the draft sat is the day it is
        // published — an opportunity cannot have started before it was posted.
        opportunity.StartsAtUtc = Schedule.StartAtPublish(opportunity.StartsAtUtc, now);
        // A blank deadline is the policy's default opportunity duration,
        // counted from the start — an opportunity that starts next week runs
        // as long as one that starts now.
        if (opportunity.DeadlineUtc is null)
        {
            var days = int.TryParse(await settings.GetAsync("opportunity.defaultDurationDays", ct), out var d) ? d : 14;
            opportunity.DeadlineUtc = Schedule.Begins(opportunity.StartsAtUtc, now).AddDays(days);
        }

        // The start, the last joining date and the milestone dates,
        // against the deadline that was just settled above — and the
        // opportunity duration between the start and that deadline.
        var scheduleProblem = Schedule.PublishProblem(
            opportunity.StartsAtUtc, opportunity.EntryCloseUtc, opportunity.DeadlineUtc.Value,
            opportunity.Milestones.OrderBy(m => m.Order).Select(m => m.DueUtc), now);
        if (scheduleProblem is not null) return Outcome.Invalid(scheduleProblem);
        // The milestone shares: all of them or none, and the whole.
        if (Rubric.WeightPublishProblem(
                opportunity.Milestones.OrderBy(m => m.Order).Select(m => m.WeightPercent).ToList()) is { } shares)
            return Outcome.Invalid(shares);

        opportunity.Status = OpportunityStatus.Open;
        opportunity.PublishedAtUtc = now;
        // Everyone who asked to hear about new opportunities hears in the
        // same save — a publish that fails announces nothing.
        await Broadcast.OpportunityOpenedAsync(db, unsubscribe, opportunity, ct);
        await db.SaveChangesAsync(ct);
        publicReads.Clear(); // on the feed from the next page, not the next interval
        emailSignal.Wake();
        pushSignal.Wake();
        return Outcome.Ok(new PublishResponse
        {
            Slug = opportunity.Slug,
            Status = OpportunityNames.StatusName(opportunity.Status),
            StartsAtUtc = opportunity.StartsAtUtc,
            DeadlineUtc = opportunity.DeadlineUtc,
            EntryCloseUtc = Schedule.EntryClosesAt(opportunity.EntryCloseUtc, opportunity.DeadlineUtc),
        });
    }

    private static string? Validate(OpportunityUpsertRequest r)
    {
        var title = r.Title?.Trim() ?? "";
        if (title.Length is < 4 or > 140) return "Title must be 4–140 characters.";
        if (Delivery.Problem(r.Delivery) is { } delivery) return delivery;
        if (Delivery.ComposeProblem(r.Delivery, r.RequiresCompose) is { } compose) return compose;
        if (r.AwardAmount is < 0 or > 1_000_000_000) return "Award amount is out of range.";
        var milestones = r.Milestones ?? [];
        if (milestones.Count > 50) return "At most 50 milestones.";
        if (milestones.Any(m => string.IsNullOrWhiteSpace(m.Title)))
            return "Every milestone needs a title.";
        if (r.MetaTitle?.Trim().Length > 80) return "The search title must stay under 80 characters.";
        if (r.MetaDescription?.Trim().Length > 200) return "The search description must stay under 200 characters.";
        if (OpportunityCategories.Problem(r.Category, r.Subcategory) is { } category) return category;
        if (OpportunityFit.MeritProblem(r.MinMeritScore) is { } merit) return merit;
        if (OpportunityFit.CleanSkills(r.Skills, out var skills) is null) return skills;
        if (Rubric.CleanRequirements(r.Requirements, out var requirements) is null) return requirements;
        if (Rubric.CleanCriteria(r.Criteria, out var criteria) is null) return criteria;
        if (Rubric.WeightProblem(milestones.Select(m => m.WeightPercent)) is { } shares) return shares;
        return Schedule.DraftProblem(
            Schedule.Day(r.StartsAtUtc), r.EntryCloseUtc, r.DeadlineUtc, milestones.Select(m => m.DueUtc));
    }

    private static void Apply(Opportunity opportunity, OpportunityUpsertRequest r)
    {
        opportunity.BriefMarkdown = r.BriefMarkdown ?? "";
        opportunity.Delivery = Delivery.Parse(r.Delivery)!.Value; // Validate() ran first
        opportunity.RequiresCompose = r.RequiresCompose == true;
        opportunity.AwardAmount = r.AwardAmount ?? 0;
        opportunity.StartsAtUtc = Schedule.Day(r.StartsAtUtc); // a day: 00:00 UTC on it
        opportunity.DeadlineUtc = r.DeadlineUtc;
        opportunity.EntryCloseUtc = r.EntryCloseUtc;
        opportunity.MetaTitle = string.IsNullOrWhiteSpace(r.MetaTitle) ? null : r.MetaTitle.Trim();
        opportunity.MetaDescription = string.IsNullOrWhiteSpace(r.MetaDescription) ? null : r.MetaDescription.Trim();
        // Validate() ran first: the pair is a real one and the list is clean.
        opportunity.Category = OpportunityCategories.CleanKey(r.Category);
        opportunity.Subcategory = opportunity.Category is null ? null : OpportunityCategories.CleanKey(r.Subcategory);
        opportunity.MinMeritScore = r.MinMeritScore ?? 0;
        opportunity.Skills.AddRange((OpportunityFit.CleanSkills(r.Skills, out _) ?? []).Select((name, i) => new OpportunitySkill
        {
            Id = Guid.NewGuid(),
            OpportunityId = opportunity.Id,
            Order = i,
            Name = name,
            Key = OpportunityFit.Key(name),
        }));
        opportunity.Milestones.AddRange((r.Milestones ?? []).Select((m, i) => new Milestone
        {
            Id = Guid.NewGuid(),
            OpportunityId = opportunity.Id,
            Order = i,
            Title = m.Title!.Trim(),
            Description = string.IsNullOrWhiteSpace(m.Description) ? null : m.Description.Trim(),
            DueUtc = m.DueUtc,
            WeightPercent = m.WeightPercent,
        }));
        opportunity.Requirements.AddRange((Rubric.CleanRequirements(r.Requirements, out _) ?? []).Select((x, i) => new OpportunityRequirement
        {
            Id = Guid.NewGuid(),
            OpportunityId = opportunity.Id,
            Order = i,
            Title = x.Title,
            Detail = x.Detail,
        }));
        opportunity.Criteria.AddRange((Rubric.CleanCriteria(r.Criteria, out _) ?? []).Select((x, i) => new OpportunityCriterion
        {
            Id = Guid.NewGuid(),
            OpportunityId = opportunity.Id,
            Order = i,
            Points = x.Points,
            Title = x.Title,
            Description = x.Description,
        }));
        // The feed counter, written in the same save as the rows it counts —
        // the request holds the full list, so no recompute round-trip needed.
        opportunity.MilestoneCount = opportunity.Milestones.Count;
    }
}

public sealed record OpportunityUpsertRequest(
    string? Title,
    string? BriefMarkdown,
    decimal? AwardAmount,
    DateTimeOffset? DeadlineUtc,
    List<MilestoneInput>? Milestones,
    string? MetaTitle,
    string? MetaDescription,
    DateTimeOffset? EntryCloseUtc = null,
    // "repository", "upload" or "both". Required: the choice is a term of
    // the deal, and a form that silently defaulted it would hide that.
    string? Delivery = null,
    // Entries must run with Docker Compose: every claim is built from the
    // repository's root compose file. Only with a repository delivery.
    bool? RequiresCompose = null,
    // What kind of work — keys from OpportunityCategories — and the door: the
    // merit score an entrant needs (0 for none) and the skills their
    // profile must list. A draft may leave all of them blank; publish
    // insists on the category.
    string? Category = null,
    string? Subcategory = null,
    int? MinMeritScore = null,
    List<string?>? Skills = null,
    // The terms beside the brief — the technical requirements table and the
    // scoring rubric. Both optional; both frozen at publish.
    List<RequirementInput>? Requirements = null,
    List<CriterionInput>? Criteria = null,
    // The competition start date — a day; any time sent is folded to 00:00
    // UTC. Blank or today starts it the moment it is published.
    DateTimeOffset? StartsAtUtc = null);

public sealed record MilestoneInput(
    string? Title, string? Description, DateTimeOffset? DueUtc = null, int? WeightPercent = null);
