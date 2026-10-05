using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Domain;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Identity;

/// <summary>
/// Identity verification, end to end: a session opened with the provider
/// for a member, the verdict that comes back — by webhook, or pulled when
/// the member returns — and the one stamp on the account the doors read.
/// Every verdict also queues its proof — the provider's whole decision and
/// copies of the document and selfie images — for
/// <see cref="IdentityProofWorker"/>, which administrators read through
/// <see cref="IdentityProofService"/>.
/// </summary>
public sealed class IdentityService(
    AppDbContext db, SettingsService settings, IdentityOptions options, IdentityProviderClient client,
    EmailWorkSignal emailSignal, IdentityProofWorkSignal proofSignal, ILogger<IdentityService> log)
{
    /// <summary>The page the provider sends the member back to; it pulls the verdict in case the webhook has not landed.</summary>
    public const string ReturnPath = "/verify/done";

    // --------------------------------------------------------------- start

    public async Task<Outcome<IdentityStartResponse>> StartAsync(
        ClaimsPrincipal principal, IdentityStartRequest? request, CancellationToken ct)
    {
        if (!await options.IsEnabledAsync(ct))
            return Outcome.Conflict("This portal is not verifying identities at the moment.");
        var config = await options.ProviderConfigAsync(ct);
        if (config is null || IdentityOptions.MissingForStart(config) is not null)
            return Outcome.Unavailable();

        var me = Principal.UserId(principal)!.Value;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == me, ct);
        if (user is null) return Outcome.Unauthorized();
        var returnTo = IdentityRules.SafeReturn(request?.ReturnTo);
        var row = await db.IdentityVerifications.SingleOrDefaultAsync(v => v.UserId == me, ct);

        // An unfinished session is picked up where it was left — a closed
        // tab, a phone put down — rather than refused or started over. The
        // provider is asked first, because it may have answered or let the
        // session lapse since; only a session it still holds open is handed
        // back, and one that lapsed falls through to a fresh one below. A
        // session with a provider the portal has since switched away from
        // is not offered: new work goes to the active provider.
        var sameProvider = row is not null && row.Provider == config.Provider;
        if (row is not null && IdentityRules.IsOpen(row.Status))
        {
            await PullVerdictAsync(row, user, ct);
            if (sameProvider && IdentityRules.IsOpen(row.Status) && row.SessionUrl is not null)
            {
                if (returnTo is not null) row.ReturnPath = returnTo;
                row.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return Outcome.Ok(new IdentityStartResponse { Url = row.SessionUrl, Resumed = true });
            }
        }
        if (row is not null && !IdentityRules.MayStart(row.Status, canResume: sameProvider && row.SessionUrl is not null))
            return Outcome.Conflict(row.Status == IdentityStatus.Approved
                ? "Your identity is already verified."
                : "The provider is reviewing your verification — you will get an email when it is decided.");

        var publicUrl = (await settings.GetAsync(WebOrigin.WebUrlKey, ct) ?? "http://localhost").TrimEnd('/');
        // Shufti Pro posts its verdicts where each request says; that is the
        // API's own address, which on a split install is not the pages'.
        var apiUrl = (await settings.GetAsync(WebOrigin.ApiUrlKey, ct))?.Trim().TrimEnd('/');
        var ask = new IdentityProviderRequests.SessionAsk(
            me,
            IdentityProviderRequests.NewReference(me),
            publicUrl + ReturnPath,
            (string.IsNullOrEmpty(apiUrl) ? publicUrl : apiUrl) + IdentityProviderRequests.ShuftiWebhookPath,
            user.Email,
            null);
        IdentityProviderRequests.Session session;
        try
        {
            session = await client.CreateSessionAsync(config, ask, ct);
        }
        catch (Exception e) when (e is IdentityProviderException or HttpRequestException or TaskCanceledException or JsonException)
        {
            // The member gets the readable line; the provider's own words
            // go to the log, which is where a key or a workflow is fixed.
            log.LogWarning(e, "Opening a verification session failed against {Provider} ({Setup}).", config.Provider, config.Setup);
            return Outcome.Conflict(e is IdentityProviderException p
                ? p.Friendly
                : "The verification provider could not be reached. Please try again in a few minutes.");
        }

        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new IdentityVerification
            {
                Id = Guid.NewGuid(),
                UserId = me,
                Provider = config.Provider,
                SessionId = session.SessionId,
                CreatedAtUtc = now,
            };
            db.IdentityVerifications.Add(row);
        }
        else
        {
            // Starting again after a decline or a lapse: the old session
            // is finished with, and its verdict goes with it — its proof
            // too, so the panel never shows one attempt's document beside
            // another's status. The activity log still holds what it said.
            row.Provider = config.Provider;
            row.SessionId = session.SessionId;
            row.Note = null;
            row.LastEventId = null;
            row.DecidedAtUtc = null;
            ClearProof(row);
            await db.IdentityDocuments.Where(d => d.VerificationId == row.Id && d.RemovedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.RemovedAtUtc, DateTimeOffset.UtcNow), ct);
            proofSignal.Wake();
        }
        row.Status = IdentityRules.MapStatus(config.Provider, session.Status);
        row.SessionUrl = session.Url;
        row.ReturnPath = returnTo;
        row.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return Outcome.Ok(new IdentityStartResponse { Url = session.Url, Resumed = false });
    }

    // -------------------------------------------------------------- status

    public async Task<Outcome<IdentityStatusResponse>> StatusAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var row = await db.IdentityVerifications.AsNoTracking().SingleOrDefaultAsync(v => v.UserId == me, ct);
        // An open session not heard of for a while is asked about, so its
        // link stops being offered once the provider has let it lapse.
        if (row is not null && IdentityRules.RecheckDue(row.Status, row.UpdatedAtUtc, DateTimeOffset.UtcNow))
            return await RefreshUserAsync(me, Principal.Role(principal), ct);
        return Outcome.Ok(await StatusOfAsync(row, Principal.Role(principal), ct));
    }

    /// <summary>
    /// The verdict, asked of the provider directly: the return page calls
    /// this so a member who closed the provider's tab before its webhook
    /// landed still sees their answer, and an administrator can ask for a
    /// member whose delivery went astray.
    /// </summary>
    public async Task<Outcome<IdentityStatusResponse>> RefreshAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        return await RefreshUserAsync(me, Principal.Role(principal), ct);
    }

    public async Task<Outcome<IdentityStatusResponse>> RefreshUserAsync(Guid userId, string? role, CancellationToken ct)
    {
        var row = await db.IdentityVerifications.Include(v => v.User).SingleOrDefaultAsync(v => v.UserId == userId, ct);
        if (row is null || row.User is null) return Outcome.Ok(await StatusOfAsync(null, role, ct));
        if (row.Status != IdentityStatus.Approved) await PullVerdictAsync(row, row.User, ct);
        return Outcome.Ok(await StatusOfAsync(row, role, ct));
    }

    /// <summary>
    /// The session's verdict read from the provider that opened it — through
    /// the active setup, or another setup of that provider when the portal
    /// has since switched — and applied. A failed read changes nothing: the
    /// stored status stands, and the webhook, when it lands, is the answer.
    /// </summary>
    private async Task PullVerdictAsync(IdentityVerification row, User user, CancellationToken ct)
    {
        var config = await options.ReaderForAsync(row.Provider, ct);
        if (config is null) return;
        try
        {
            var decision = await client.ReadDecisionAsync(config, row.SessionId, ct);
            if (NoVerdict(row.Provider, decision.Status))
            {
                row.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }
            await ApplyVerdictAsync(row, user, decision.Status, decision.Reason, eventId: null, ct);
        }
        catch (Exception e) when (e is IdentityProviderException or HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(e, "Reading a verification decision failed against {Provider}.", config.Provider);
            // Counted as a check all the same, so a provider that is down
            // is not asked again on every status read until it recovers.
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<IdentityStatusResponse> StatusOfAsync(IdentityVerification? row, string? role, CancellationToken ct)
    {
        var enabled = await options.IsEnabledAsync(ct);
        var doors = new IdentityDoors
        {
            Publish = enabled && await options.RequiredForPublishAsync(ct),
            Apply = enabled && await options.RequiredForApplyAsync(ct),
            Payments = enabled && await options.RequiredForPaymentsAsync(ct),
        };
        return new IdentityStatusResponse
        {
            Status = IdentityRules.StatusName(row?.Status),
            VerifiedAtUtc = row?.Status == IdentityStatus.Approved ? row.DecidedAtUtc : null,
            Note = row?.Status == IdentityStatus.Declined ? row.Note : null,
            ContinueUrl = row is not null && IdentityRules.IsOpen(row.Status) ? row.SessionUrl : null,
            ReturnTo = row?.ReturnPath,
            Enabled = enabled,
            Required = await options.RequiredForRoleAsync(role, ct),
            RequiredFor = doors,
        };
    }

    // ------------------------------------------------------------- webhook

    /// <param name="payload">The body exactly as it arrived — the signature is over these bytes.</param>
    /// <param name="signature">The X-Signature header.</param>
    /// <param name="timestamp">The X-Timestamp header.</param>
    public async Task<Outcome<IdentityWebhookResponse>> ReceiveAsync(
        byte[] payload, string? signature, string? timestamp, CancellationToken ct)
    {
        // Every Didit setup's secret, active or not: a session opened under
        // a setup since switched off still reports back through it.
        var secrets = (await settings.SetupsAsync(Setups.Identity, ct))
            .Where(s => (s.Get(IdentityKeys.Provider)?.Trim().ToLowerInvariant() ?? "") is "" or IdentityProviders.Didit)
            .Select(s => s.Get(IdentityKeys.WebhookSecret))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (secrets.Count == 0)
        {
            log.LogWarning("Identity webhook received but no identity setup has a webhook secret; dropping it.");
            return Outcome.Unavailable();
        }
        var now = DateTimeOffset.UtcNow;
        if (!secrets.Any(secret => IdentityWebhookSignature.Verify(secret!, payload, signature, timestamp, now)))
        {
            log.LogWarning("Identity webhook signature or timestamp did not verify; dropping the delivery.");
            return Outcome.Unauthorized();
        }

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        var eventId = Text(root, "event_id");
        var webhookType = Text(root, "webhook_type") ?? "session";
        var sessionId = Text(root, "session_id");
        var status = Text(root, "status");
        if (eventId is null || sessionId is null || status is null)
            return Outcome.Invalid("Missing event_id, session_id or status.");

        // Stamp the delivery first. The unique index on DeliveryId turns a
        // redelivery into a constraint hit, and the provider retries.
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            Source = WebhookDelivery.Identity,
            DeliveryId = "didit:" + eventId,
            Event = webhookType,
            Action = status,
            Payload = Encoding.UTF8.GetString(payload),
            ReceivedAtUtc = now,
        };
        db.WebhookDeliveries.Add(delivery);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            return Outcome.Ok(new IdentityWebhookResponse { Ok = true, Note = "duplicate delivery" });
        }

        var note = await HandleAsync(sessionId, status, IdentityProviderRequests.DeclineReason(root), eventId, ct);
        delivery.HandledNote = note;
        await db.SaveChangesAsync(ct);
        return Outcome.Ok(new IdentityWebhookResponse { Ok = true, Note = note });
    }

    /// <summary>
    /// A Shufti Pro callback. It is signed with a setup's own secret key
    /// (<see cref="ShuftiSignature"/>) — every Shufti Pro setup is tried,
    /// active or not — and carries no time, so the body's hash is its
    /// delivery id: the same callback twice is taken once.
    /// </summary>
    /// <param name="payload">The body exactly as it arrived — the signature is over these bytes.</param>
    /// <param name="signature">The Signature header.</param>
    public async Task<Outcome<IdentityWebhookResponse>> ReceiveShuftiAsync(byte[] payload, string? signature, CancellationToken ct)
    {
        var keys = (await options.AllConfigsAsync(ct))
            .Where(c => c.Provider == IdentityProviders.ShuftiPro)
            .Select(c => c.ApiKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keys.Count == 0)
        {
            log.LogWarning("Shufti Pro callback received but no Shufti Pro setup has a secret key; dropping it.");
            return Outcome.Unavailable();
        }
        if (!keys.Any(key => ShuftiSignature.Verify(key, payload, signature)))
        {
            log.LogWarning("Shufti Pro callback signature did not verify; dropping the delivery.");
            return Outcome.Unauthorized();
        }

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        var reference = Text(root, "reference");
        var shuftiEvent = Text(root, "event");
        if (reference is null || shuftiEvent is null)
            return Outcome.Invalid("Missing reference or event.");

        var deliveryId = ShuftiSignature.DeliveryId(payload);
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            Source = WebhookDelivery.Identity,
            DeliveryId = deliveryId,
            Event = "callback",
            Action = shuftiEvent,
            Payload = Encoding.UTF8.GetString(payload),
            ReceivedAtUtc = DateTimeOffset.UtcNow,
        };
        db.WebhookDeliveries.Add(delivery);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            return Outcome.Ok(new IdentityWebhookResponse { Ok = true, Note = "duplicate delivery" });
        }

        var note = await HandleAsync(reference, shuftiEvent,
            IdentityProviderRequests.DeclineReason(IdentityProviders.ShuftiPro, root), deliveryId, ct);
        delivery.HandledNote = note;
        await db.SaveChangesAsync(ct);
        return Outcome.Ok(new IdentityWebhookResponse { Ok = true, Note = note });
    }

    /// <summary>
    /// The whole dispatch, shared with the admin console's replay — a
    /// replayed delivery must take exactly the live path. A Shufti Pro event
    /// that says only "something changed" has the request's status read
    /// back instead; one that is no verdict at all is noted and left.
    /// </summary>
    public async Task<string> HandleAsync(string sessionId, string status, string? reason, string? eventId, CancellationToken ct)
    {
        var row = await db.IdentityVerifications.Include(v => v.User).SingleOrDefaultAsync(v => v.SessionId == sessionId, ct);
        if (row is null || row.User is null) return "no matching verification";
        if (eventId is not null && row.LastEventId == eventId) return "already applied";
        var before = row.Status;
        if (row.Provider == IdentityProviders.ShuftiPro && ShuftiEvents.Ignored(status))
            return $"{status}: no verdict, left {IdentityRules.StatusName(row.Status)}";
        if (row.Provider == IdentityProviders.ShuftiPro && ShuftiEvents.ReadBack(status))
        {
            if (eventId is not null) row.LastEventId = eventId;
            await PullVerdictAsync(row, row.User, ct);
            return before == row.Status
                ? $"{status}: read back, still {IdentityRules.StatusName(row.Status)}"
                : $"{status}: read back, {IdentityRules.StatusName(before)} → {IdentityRules.StatusName(row.Status)}";
        }
        await ApplyVerdictAsync(row, row.User, status, reason, eventId, ct);
        return before == row.Status
            ? $"still {IdentityRules.StatusName(row.Status)}"
            : $"{IdentityRules.StatusName(before)} → {IdentityRules.StatusName(row.Status)}";
    }

    /// <summary>
    /// The provider's word applied: the row, the stamp on the account, and
    /// the email — in the caller's save, so a verdict that fails to commit
    /// announces nothing. An approval stamps; a later decline or lapse
    /// takes the stamp back, so the doors read the provider's current view.
    /// </summary>
    private async Task ApplyVerdictAsync(
        IdentityVerification row, User user, string providerStatus, string? reason, string? eventId, CancellationToken ct)
    {
        var status = IdentityRules.MapStatus(row.Provider, providerStatus);
        var now = DateTimeOffset.UtcNow;
        var changed = status != row.Status;
        row.Status = status;
        row.UpdatedAtUtc = now;
        if (eventId is not null) row.LastEventId = eventId;
        if (IdentityRules.IsFinal(status)) row.DecidedAtUtc ??= now;
        // The provider's page is only worth offering while the member can
        // still finish there; answered, under review or lapsed, it goes.
        if (!IdentityRules.IsOpen(status)) row.SessionUrl = null;

        if (status == IdentityStatus.Approved)
        {
            row.Note = null;
            user.IdentityVerifiedAtUtc ??= now;
        }
        else
        {
            row.Note = status == IdentityStatus.Declined ? IdentityRules.Note(reason) : null;
            if (status is IdentityStatus.Declined or IdentityStatus.Expired) user.IdentityVerifiedAtUtc = null;
        }

        // Every verdict that has a decision behind it keeps its proof: a
        // change of status, or one whose proof was never copied.
        var queued = IdentityProof.Due(status, changed, row.DecisionJson is not null,
            triedBefore: row.ProofDueAtUtc is not null || row.ProofError is not null);
        if (queued)
        {
            row.ProofDueAtUtc = now;
            row.ProofAttempts = 0;
            row.ProofError = null;
        }

        if (changed && status is IdentityStatus.Approved or IdentityStatus.Declined)
        {
            var portalName = await settings.GetAsync("branding.portalName", ct) ?? "Winners Portal";
            Notify.Queue(db, user,
                status == IdentityStatus.Approved ? "identity_verified" : "identity_declined",
                status == IdentityStatus.Approved
                    ? Emails.IdentityVerified(portalName, user.Role)
                    : Emails.IdentityDeclined(portalName, row.Note),
                dedupeKey: $"identity:{row.SessionId}:{IdentityRules.StatusName(status)}");
        }
        await db.SaveChangesAsync(ct);
        if (changed) emailSignal.Wake();
        if (queued) proofSignal.Wake();
    }

    /// <summary>A verification's proof forgotten: the decision and the worker's state. Its images are marked by the caller.</summary>
    public static void ClearProof(IdentityVerification row)
    {
        row.DecisionJson = null;
        row.DecisionReadAtUtc = null;
        row.ProofDueAtUtc = null;
        row.ProofAttempts = 0;
        row.ProofError = null;
    }

    // ---------------------------------------------------------------- admin

    /// <summary>
    /// An administrator takes a verification back: the row goes, the stamp
    /// goes, and the member may start again. Its stored images are left
    /// without a verification, which is what the proof worker deletes.
    /// </summary>
    public async Task<Outcome> ResetAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.ErasedAtUtc == null, ct);
        if (user is null) return Outcome.NotFound();
        user.IdentityVerifiedAtUtc = null;
        await db.IdentityVerifications.Where(v => v.UserId == userId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        proofSignal.Wake();
        return Outcome.NoContent();
    }

    // ----------------------------------------------------------------- test

    /// <summary>
    /// Proves one setup: the key is accepted by the provider, the workflow
    /// id has the shape of one, the webhook secret is set. A made-up
    /// session asked for its decision answers 404 to a good key and 401 or
    /// 403 to a bad one — no session is opened and nothing is billed.
    /// </summary>
    public async Task<(bool Ok, string Detail)> TestAsync(string setupId, CancellationToken ct)
    {
        var config = await options.ProviderConfigAsync(setupId, ct);
        if (config is null)
            return (false, "This setup has no key saved — add one above and save first.");
        if (IdentityProviders.Find(config.Provider) is null)
            return (false, $"“{config.Provider}” is not a provider this portal knows — choose one from the list.");
        var label = IdentityProviders.LabelOf(config.Provider);
        if (config.Provider == IdentityProviders.Didit)
        {
            if (!Guid.TryParse(config.WorkflowId, out _))
                return (false, "The workflow ID is not the UUID the provider's console shows — copy it again.");
            if (string.IsNullOrWhiteSpace(config.WebhookSecret))
                return (false, "No webhook secret is saved — verdicts would arrive only when a member returns to the portal.");
        }
        if (config.Provider == IdentityProviders.ShuftiPro && config.ClientId.Length == 0)
            return (false, "No client ID is saved — Shufti Pro signs in with the client ID and secret key together.");
        try
        {
            var (status, body) = await client.ProbeAsync(
                IdentityProviderRequests.ReadDecision(config, IdentityProviderRequests.NewReference(Guid.Empty)), ct);
            return IdentityRules.ProbeAnswer(config.Provider, label, config.Setup, status, body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(e, "The identity connection test could not reach {Provider}.", config.Provider);
            return (false, "The provider could not be reached from this server. The API log has the connection error.");
        }
    }

    /// <summary>Whether a provider's word is no verdict to apply: Shufti Pro's "something changed" and "deleted" events.</summary>
    private static bool NoVerdict(string provider, string status) =>
        provider == IdentityProviders.ShuftiPro && (ShuftiEvents.ReadBack(status) || ShuftiEvents.Ignored(status));

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
