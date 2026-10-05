using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Preview;

/// <summary>
/// The buttons of a preview: read it, start it, stop it, open it. None of
/// them calls the build host — a start or a stop is a row the preview
/// worker carries there, so a slow host never holds a request. Opening is
/// a redirect to the preview's own address with a one-time ticket
/// (<see cref="PreviewTickets"/>), minted only for someone who may see it.
///
/// Who may: the entrant, the opportunity's client and administrators
/// (<see cref="Previews.CanRun"/>). Anybody else gets the same 404 as for a
/// preview that does not exist. A milestone's preview is keyed by its
/// checkpoint, the final's by its entry; either id opens the preview.
/// </summary>
public sealed class PreviewService(AppDbContext db, BuildHostService buildHost, SettingsService settings, PreviewWorkSignal signal)
{
    // --------------------------------------------------------------- read

    public async Task<Outcome<PreviewResponse>> ReadAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var subject = await SubjectAsync(id, principal, ct);
        if (subject.Refusal is { } refusal) return refusal;
        var host = await buildHost.StateAsync(ct);
        var row = await db.Previews.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        return Outcome.Ok(await ResponseAsync(subject.Of!, row, host, ct));
    }

    // -------------------------------------------------------------- start

    public async Task<Outcome<PreviewResponse>> StartAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var subject = await SubjectAsync(id, principal, ct);
        if (subject.Refusal is { } refusal) return refusal;
        var s = subject.Of!;
        var host = await buildHost.StateAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var row = await db.Previews.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (row is { StopRequested: true })
            return Outcome.Conflict("This preview is stopping — start it again in a moment.");
        if (row is { Status: PreviewStatus.Pending or PreviewStatus.Starting or PreviewStatus.Running })
            return Outcome.Ok(await ResponseAsync(s, row, host, ct)); // already on its way: one press is enough

        if (await StartProblemAsync(s, host, ct) is { } problem) return Outcome.Conflict(problem);
        var config = host.Config!; // StartProblemAsync refused a start without one
        var busy = await db.Previews.CountAsync(p => !p.StopRequested && p.Id != id
            && (p.Status == PreviewStatus.Pending || p.Status == PreviewStatus.Starting || p.Status == PreviewStatus.Running), ct);
        if (busy >= config.MaxRunning) return Outcome.Conflict(Previews.MaxRunningText(config.MaxRunning));

        if (row is null)
        {
            row = new Domain.Preview { Id = id, EntryId = s.EntryId, CheckpointId = s.CheckpointId, Ref = s.Ref! };
            db.Previews.Add(row);
        }
        row.Ref = s.Ref!;
        row.Sha = null;
        row.Status = PreviewStatus.Pending;
        row.Attempts = 0;
        row.DueAtUtc = now;
        row.RequestedByUserId = s.ViewerId;
        row.RequestedAtUtc = now;
        row.StartedAtUtc = null;
        row.LastSeenAtUtc = null;
        row.StoppedAtUtc = null;
        row.StopRequested = false;
        row.StopReason = null;
        row.Error = null;
        row.LogTail = null;
        row.Notes = null;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two presses at once: the other one's row stands.
            return Outcome.Conflict("This preview is already being started.");
        }
        signal.Wake();
        return Outcome.Ok(await ResponseAsync(s, row, host, ct));
    }

    // --------------------------------------------------------------- stop

    public async Task<Outcome<PreviewResponse>> StopAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var subject = await SubjectAsync(id, principal, ct);
        if (subject.Refusal is { } refusal) return refusal;
        var row = await db.Previews.SingleOrDefaultAsync(p => p.Id == id, ct);
        var host = await buildHost.StateAsync(ct);
        var config = host.Config;
        if (row is null) return Outcome.Ok(await ResponseAsync(subject.Of!, null, host, ct));

        if (row.Status == PreviewStatus.Pending)
        {
            // Never handed over: nothing on the host to take down.
            row.Status = PreviewStatus.Stopped;
            row.StoppedAtUtc = DateTimeOffset.UtcNow;
            row.DueAtUtc = null;
            row.StopReason = Previews.StopReasonText("requested", config?.IdleMinutes ?? PreviewHost.DefaultIdleMinutes);
            await db.SaveChangesAsync(ct);
        }
        else if (row.Status is PreviewStatus.Starting or PreviewStatus.Running && !row.StopRequested)
        {
            row.StopRequested = true;
            await db.SaveChangesAsync(ct);
            signal.Wake();
        }
        return Outcome.Ok(await ResponseAsync(subject.Of!, row, host, ct));
    }

    // --------------------------------------------------------------- open

    /// <summary>
    /// A plain link, because auth rides the cookie: the viewer is checked
    /// here, and the answer is a redirect to the preview's own address with
    /// a ticket the host trades for a cookie of its own.
    /// </summary>
    public async Task<Outcome> OpenAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var subject = await SubjectAsync(id, principal, ct);
        if (subject.Refusal is { } refusal) return refusal.Untyped;
        var row = await db.Previews.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (row is not { Status: PreviewStatus.Running, StopRequested: false }) return Outcome.Conflict(Previews.NotRunning);

        var config = (await buildHost.StateAsync(ct)).Config;
        if (config?.RunUrl is not { } runUrl) return Outcome.Conflict(Previews.NoAddress);
        if (PreviewHost.RunDomainProblem(runUrl, PreviewHost.ParseWebUrl(await settings.GetAsync(WebOrigin.WebUrlKey, ct))) is { } domain)
            return Outcome.Conflict(domain);

        var ticket = PreviewTickets.Mint(config.Token, PreviewHost.Label(id), DateTimeOffset.UtcNow);
        return Outcome.Redirect(PreviewTickets.Url(PreviewHost.Address(runUrl, id), ticket).ToString());
    }

    // ------------------------------------------------------------- shared

    /// <summary>What a preview id stands for: a claimed milestone or an entry's final, and who is asking.</summary>
    private sealed record Subject(
        Guid ViewerId, Guid EntryId, Guid? CheckpointId, int? MilestoneNumber, string? Ref,
        PreviewBuildStatus? BuildStatus, OpportunityStatus OpportunityStatus, bool RequiresCompose, bool Frozen, EntryStatus EntryStatus,
        bool ViewerIsEntrant);

    private sealed record Resolved(Subject? Of, Outcome<PreviewResponse>? Refusal);

    private async Task<Resolved> SubjectAsync(Guid id, ClaimsPrincipal principal, CancellationToken ct)
    {
        var viewerId = Principal.UserId(principal);
        if (viewerId is null) return new(null, Outcome.Unauthorized());
        var isAdmin = principal.IsInRole(Roles.Admin);

        var claim = await db.Checkpoints.AsNoTracking()
            .Where(cp => cp.Id == id)
            .Select(cp => new
            {
                cp.EntryId, cp.CommitSha, cp.BuildStatus, Order = cp.Milestone!.Order,
                cp.Entry!.FreelancerId, cp.Entry.Status, cp.Entry.FrozenAtUtc,
                cp.Entry.Opportunity!.ClientId, OpportunityStatus = cp.Entry.Opportunity.Status, cp.Entry.Opportunity.RequiresCompose,
            })
            .SingleOrDefaultAsync(ct);
        if (claim is not null)
        {
            return Previews.CanRun(claim.FreelancerId == viewerId, claim.ClientId == viewerId, isAdmin)
                ? new(new Subject(viewerId.Value, claim.EntryId, id, claim.Order + 1, claim.CommitSha, claim.BuildStatus,
                    claim.OpportunityStatus, claim.RequiresCompose, claim.FrozenAtUtc != null, claim.Status,
                    claim.FreelancerId == viewerId), null)
                : new(null, Outcome.NotFound());
        }

        var entry = await db.Entries.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.FreelancerId, e.Status, e.FrozenAtUtc, e.Opportunity!.ClientId, OpportunityStatus = e.Opportunity.Status, e.Opportunity.RequiresCompose })
            .SingleOrDefaultAsync(ct);
        return entry is not null && Previews.CanRun(entry.FreelancerId == viewerId, entry.ClientId == viewerId, isAdmin)
            ? new(new Subject(viewerId.Value, id, null, null, "final", null,
                entry.OpportunityStatus, entry.RequiresCompose, entry.FrozenAtUtc != null, entry.Status,
                entry.FreelancerId == viewerId), null)
            : new(null, Outcome.NotFound());
    }

    /// <summary>Why a start would be refused now, or null — the same sentence the panel shows beside a disabled button.</summary>
    private async Task<string?> StartProblemAsync(Subject s, BuildHostState host, CancellationToken ct)
    {
        if (!host.Ready) return Previews.PreviewsOff;
        if (host.Config?.RunUrl is not { } runUrl) return Previews.NoAddress;
        if (PreviewHost.RunDomainProblem(runUrl, PreviewHost.ParseWebUrl(await settings.GetAsync(WebOrigin.WebUrlKey, ct))) is { } domain)
            return domain;
        if (s.CheckpointId is not null)
            return s.BuildStatus == PreviewBuildStatus.Built && s.Ref is not null ? null : Previews.BuildsFirst;
        return Previews.FinalProblem(s.OpportunityStatus, s.RequiresCompose, s.Frozen, s.EntryStatus);
    }

    private async Task<PreviewResponse> ResponseAsync(Subject s, Domain.Preview? row, BuildHostState host, CancellationToken ct)
    {
        var config = host.Config;
        var status = row?.Status ?? PreviewStatus.None;
        var live = status is PreviewStatus.Pending or PreviewStatus.Starting or PreviewStatus.Running;
        var problem = live ? null : await StartProblemAsync(s, host, ct);
        var commit = row?.Sha ?? (s.CheckpointId is not null ? s.Ref : null);
        return new PreviewResponse
        {
            Id = row?.Id ?? s.CheckpointId ?? s.EntryId,
            Status = Previews.StatusName(status),
            Kind = s.CheckpointId is null ? "final" : "milestone",
            MilestoneNumber = s.MilestoneNumber,
            Commit = commit is { Length: > 7 } ? commit[..7] : commit,
            CanStart = !live && problem is null,
            StartProblem = problem,
            Address = status == PreviewStatus.Running && config?.RunUrl is { } runUrl
                ? PreviewHost.Address(runUrl, row!.Id).Authority
                : null,
            RequestedAtUtc = row?.RequestedAtUtc,
            StartedAtUtc = row?.StartedAtUtc,
            LastSeenAtUtc = row?.LastSeenAtUtc,
            StoppedAtUtc = row?.StoppedAtUtc,
            Stopping = row?.StopRequested ?? false,
            StopReason = status == PreviewStatus.Stopped ? row?.StopReason : null,
            Error = status == PreviewStatus.Failed ? row?.Error : null,
            LogTail = row?.LogTail,
            Notes = status == PreviewStatus.Running ? row?.Notes : null,
            IdleMinutes = config?.IdleMinutes ?? PreviewHost.DefaultIdleMinutes,
            ViewerIsEntrant = s.ViewerIsEntrant,
        };
    }
}
