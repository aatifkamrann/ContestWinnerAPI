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

    public async Task<Outcome<IdentityStartResponse>> StartAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!await options.IsEnabledAsync(ct))
            return Outcome.Conflict("This portal is not verifying identities at the moment.");
        var config = await options.ProviderConfigAsync(ct);
        if (config is null || config.WorkflowId.Length == 0)
            return Outcome.Unavailable();

        var me = Principal.UserId(principal)!.Value;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == me, ct);
        if (user is null) return Outcome.Unauthorized();
        var row = await db.IdentityVerifications.SingleOrDefaultAsync(v => v.UserId == me, ct);
        if (row is not null && !IdentityRules.MayStart(row.Status))
            return Outcome.Conflict(row.Status == IdentityStatus.Approved
                ? "Your identity is already verified."
                : "A verification is already under way — finish it, or wait for the provider's answer.");

        var publicUrl = (await settings.GetAsync("branding.publicUrl", ct) ?? "http://localhost").TrimEnd('/');
        IdentityProviderRequests.Session session;
        try
        {
            session = await client.CreateSessionAsync(
                config.Provider, config.ApiKey, config.WorkflowId, me, publicUrl + ReturnPath, user.Email, null, ct);
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
        row.Status = IdentityRules.MapStatus(session.Status);
        row.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return Outcome.Ok(new IdentityStartResponse { Url = session.Url });
    }

    // -------------------------------------------------------------- status

    public async Task<Outcome<IdentityStatusResponse>> StatusAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var me = Principal.UserId(principal)!.Value;
        var row = await db.IdentityVerifications.AsNoTracking().SingleOrDefaultAsync(v => v.UserId == me, ct);
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
        if (row.Status != IdentityStatus.Approved)
        {
            var config = await options.ProviderConfigAsync(ct);
            if (config is not null && config.Provider == row.Provider)
            {
                try
                {
                    var decision = await client.ReadDecisionAsync(config.Provider, config.ApiKey, row.SessionId, ct);
                    await ApplyVerdictAsync(row, row.User, decision.Status, decision.Reason, eventId: null, ct);
                }
                catch (Exception e) when (e is IdentityProviderException or HttpRequestException or TaskCanceledException or JsonException)
                {
                    // Nothing changes; the stored status stands and the
                    // webhook, when it lands, is the answer.
                    log.LogWarning(e, "Reading a verification decision failed against {Provider}.", config.Provider);
                }
            }
        }
        return Outcome.Ok(await StatusOfAsync(row, role, ct));
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
        // Every setup's secret, active or not: a session opened under a
        // setup since switched off still reports back through it.
        var secrets = (await settings.SetupsAsync(Setups.Identity, ct))
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

    /// <summary>The whole dispatch, shared with the admin console's replay — a replayed delivery must take exactly the live path.</summary>
    public async Task<string> HandleAsync(string sessionId, string status, string? reason, string? eventId, CancellationToken ct)
    {
        var row = await db.IdentityVerifications.Include(v => v.User).SingleOrDefaultAsync(v => v.SessionId == sessionId, ct);
        if (row is null || row.User is null) return "no matching verification";
        if (eventId is not null && row.LastEventId == eventId) return "already applied";
        var before = row.Status;
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
        var status = IdentityRules.MapStatus(providerStatus);
        var now = DateTimeOffset.UtcNow;
        var changed = status != row.Status;
        row.Status = status;
        row.UpdatedAtUtc = now;
        if (eventId is not null) row.LastEventId = eventId;
        if (IdentityRules.IsFinal(status)) row.DecidedAtUtc ??= now;

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
            return (false, "This setup has no API key saved — add one above and save first.");
        if (!Guid.TryParse(config.WorkflowId, out _))
            return (false, "The workflow ID is not the UUID the provider's console shows — copy it again.");
        if (string.IsNullOrWhiteSpace(config.WebhookSecret))
            return (false, "No webhook secret is saved — verdicts would arrive only when a member returns to the portal.");
        try
        {
            var status = await client.ProbeAsync(
                IdentityProviderRequests.ReadDecision(config.Provider, config.ApiKey, Guid.NewGuid().ToString()), ct);
            return status switch
            {
                404 => (true, $"{config.Provider} accepted the key (“{config.Setup}”). Workflow and webhook secret are set; the first real verification proves the workflow."),
                401 or 403 => (false, $"{config.Provider} rejected the key ({status}) — check it in the provider's console."),
                429 => (false, $"{config.Provider} is rate-limiting this portal ({status}) — try again in a minute."),
                >= 500 => (false, $"{config.Provider} is not available right now ({status})."),
                _ => (false, $"{config.Provider} answered {status} to the probe; the key may still be fine — try a real verification."),
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(e, "The identity connection test could not reach {Provider}.", config.Provider);
            return (false, "The provider could not be reached from this server. The API log has the connection error.");
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
