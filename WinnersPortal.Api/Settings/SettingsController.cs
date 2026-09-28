using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Auth;
using WinnersPortal.Api.Common;
using WinnersPortal.Services.Ai;
using WinnersPortal.Services.Auth;
using WinnersPortal.Services.Common;
using WinnersPortal.Services.Email;
using WinnersPortal.Services.GitHub;
using WinnersPortal.Services.Identity;
using WinnersPortal.Services.Phone;
using WinnersPortal.Services.Preview;
using WinnersPortal.Services.Settings;
using WinnersPortal.Services.Storage;

namespace WinnersPortal.Api.Settings;

/// <summary>The HTTP edge of <see cref="SettingsAdminService"/>: binds the request, answers with its outcome.</summary>
[ApiController]
public sealed class SettingsController(SettingsAdminService settingsAdmin) : ControllerBase
{
    // ------------------------------------------------------------ logo
    // The mark itself, uploaded rather than linked: it outranks the Logo
    // URL, and removing it falls back to that. The tab icon is its own
    // slot because the shapes are different jobs, and falls back to the
    // logo. Sniffed, not trusted — the content type is what the browser
    // says, the bytes are what is there — and kept in a system setting
    // so a portal with no object storage configured can still wear its
    // own marks. Two slots on the same rules, /logo and /icon: a literal
    // route each, so the activity log names them apart, over one method.
    [HttpPost("api/settings/logo")]
    [Authorize(Policy = "admin")]
    public Task<IResult> PostLogo(IFormFile file, [FromServices] SettingsService settings, CancellationToken ct) =>
        UploadBrandImageAsync("logo", file, settings, ct);

    [HttpPost("api/settings/icon")]
    [Authorize(Policy = "admin")]
    public Task<IResult> PostIcon(IFormFile file, [FromServices] SettingsService settings, CancellationToken ct) =>
        UploadBrandImageAsync("icon", file, settings, ct);

    [HttpDelete("api/settings/logo")]
    [Authorize(Policy = "admin")]
    public Task<IResult> DeleteLogo([FromServices] SettingsService settings, CancellationToken ct) =>
        RemoveBrandImageAsync("logo", settings, ct);

    [HttpDelete("api/settings/icon")]
    [Authorize(Policy = "admin")]
    public Task<IResult> DeleteIcon([FromServices] SettingsService settings, CancellationToken ct) =>
        RemoveBrandImageAsync("icon", settings, ct);

    private async Task<IResult> UploadBrandImageAsync(string slot, IFormFile file, SettingsService settings, CancellationToken ct)
    {
        var image = BrandImage.All.Single(i => i.Name == slot);
        if (Logo.Problem(file.ContentType, file.Length) is { } problem)
            return Results.BadRequest(new ErrorResponse(problem));
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var type = Logo.Sniff(bytes);
        if (type is null)
            return Results.BadRequest(new ErrorResponse(
                $"That file is not an image the portal can serve — use a {Logo.TypesSentence}."));
        var stamp = Logo.NewStamp();
        await settings.SetManyAsync(
            new Dictionary<string, string?> { [image.DataKey] = Logo.Encode(type, bytes), [image.StampKey] = stamp },
            changedBy: Principal.Email(User) ?? "admin",
            allowSystem: true,
            ct: ct);
        return Results.Ok(new BrandImageResponse { Url = image.PublicPath(stamp), ContentType = type, Bytes = bytes.Length });
    }

    private async Task<IResult> RemoveBrandImageAsync(string slot, SettingsService settings, CancellationToken ct)
    {
        var image = BrandImage.All.Single(i => i.Name == slot);
        await settings.SetManyAsync(
            new Dictionary<string, string?> { [image.DataKey] = null, [image.StampKey] = null },
            changedBy: Principal.Email(User) ?? "admin",
            allowSystem: true,
            ct: ct);
        return Results.NoContent();
    }

    [HttpGet("api/settings")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetSettings(CancellationToken ct) =>
        (await settingsAdmin.ReadAsync(ct)).ToResult();

    [HttpPut("api/settings")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PutSettings(UpdateSettingsRequest request, CancellationToken ct) =>
        (await settingsAdmin.SaveAsync(request, User, ct)).ToResult();

    // The Redis settings bind at startup too: the panel says what this
    // process does with Redis, and when what is saved is not what it started with.
    [HttpGet("api/settings/redis-status")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetRedisStatus(CancellationToken ct) =>
        (await settingsAdmin.RedisStatusAsync(ct)).ToResult();

    // The JWT settings bind at startup, so the panel says when what is
    // saved is not yet what the running API signs with.
    [HttpGet("api/settings/jwt-status")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> GetJwtStatus(CancellationToken ct) =>
        (await settingsAdmin.JwtStatusAsync(ct)).ToResult();

    // Every session ends, this one included: its cookie goes with the
    // answer, rather than failing on the page's next request.
    [HttpPost("api/settings/revoke-sessions")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostRevokeSessions(CancellationToken ct)
    {
        var outcome = await settingsAdmin.RevokeAllSessionsAsync(ct);
        SessionCookie.Clear(HttpContext);
        return outcome.ToResult();
    }

    // The wizard's line, honoured at last: "tested by sending to the
    // administrator who is standing right there." One click, one real
    // email to whoever pressed the button — a wrong SMTP password or API key
    // answers here, not as a notification that silently never arrives.
    // Every test below proves one setup, named by ?setup= — active or
    // not, since proving one before it takes work is the point. With no
    // setup named, the active one. Each answer is kept as that setup's
    // last test (SetupTestLog), against the values read before it ran,
    // and comes back as `last` for the badge on its card.
    [HttpPost("api/settings/test-email")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostTestEmail(string? setup, [FromServices] EmailSender sender, [FromServices] SettingsService settings, [FromServices] SetupTestLog tests, CancellationToken ct)
    {
        var email = Principal.Email(User);
        if (string.IsNullOrWhiteSpace(email))
            return Results.BadRequest(new ErrorResponse("Your session carries no email address — sign in again."));
        var name = Principal.Name(User) ?? "Administrator";
        var id = await TestedAsync(settings, Setups.Email, setup, ct);
        var values = await settings.SetupAsync(Setups.Email, id, ct);
        var (ok, detail) = await sender.TestAsync(id, name, email, ct);
        var last = await tests.RecordAsync(Setups.Email, values, ok, detail, email, ct);
        return Results.Ok(new SetupTestResponse { Ok = ok, Detail = detail, Last = last });
    }

    // The phone group proves itself the same way as email, to a number
    // the administrator types — their own, usually. The message is the
    // real code text with a real code in it, so what the phone shows
    // is what a new member will see.
    [HttpPost("api/settings/test-phone")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostTestPhone(string? setup, TestPhoneRequest request, [FromServices] PhoneSender phones, [FromServices] SettingsService settings, [FromServices] SetupTestLog tests, CancellationToken ct)
    {
        var (to, problem) = Confirmation.CleanPhone(request.To);
        if (to is null)
            return Results.BadRequest(new ErrorResponse(problem ?? "Enter the number to text."));
        var portalName = await settings.GetAsync("branding.portalName", ct) ?? "Winners Portal";
        var id = await TestedAsync(settings, Setups.Phone, setup, ct);
        var values = await settings.SetupAsync(Setups.Phone, id, ct);
        var (ok, detail) = await phones.SendThroughAsync(
            id, to, Confirmation.Text(portalName, Confirmation.NewCode()), ct);
        var last = await tests.RecordAsync(Setups.Phone, values, ok,
            SetupTestLog.WithoutNumber(detail, to), Principal.Email(User), ct);
        return Results.Ok(new SetupTestResponse
        {
            Ok = ok,
            Detail = ok ? $"{detail} Now check {to} — the text should be there within a minute." : detail,
            Last = last,
        });
    }

    // The group the blueprint singled out and the build then skipped: a
    // mis-pasted PEM is the likeliest reason a fresh install does not
    // work, and it should say so here in ten seconds — not two days
    // later, as an entrant wondering where their repository is.
    [HttpPost("api/settings/test-github")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostTestGithub(string? setup, [FromServices] GitHubService github, [FromServices] SettingsService settings, [FromServices] SetupTestLog tests, CancellationToken ct)
    {
        var id = await TestedAsync(settings, Setups.GitHub, setup, ct);
        var values = await settings.SetupAsync(Setups.GitHub, id, ct);
        var (ok, detail) = await github.TestAsync(id, ct);
        var last = await tests.RecordAsync(Setups.GitHub, values, ok, detail, Principal.Email(User), ct);
        return Results.Ok(new SetupTestResponse { Ok = ok, Detail = detail, Last = last });
    }

    // The blueprint's storage step "proves itself": upload, read back
    // through a signed URL, delete — the exact path every file takes.
    // Runs against the *saved* settings, which is the point: it tests
    // what the portal will actually use. The page's Origin rides along
    // so a public URL on http is refused under a page on https.
    [HttpPost("api/settings/test-storage")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostTestStorage(string? setup, [FromServices] StorageService storage, [FromServices] SettingsService settings, [FromServices] SetupTestLog tests, CancellationToken ct)
    {
        var id = await TestedAsync(settings, Setups.Storage, setup, ct);
        var values = await settings.SetupAsync(Setups.Storage, id, ct);
        var (ok, detail) = await storage.TestAsync(id, Request.Headers.Origin.ToString(), ct);
        var last = await tests.RecordAsync(Setups.Storage, values, ok, detail, Principal.Email(User), ct);
        return Results.Ok(new SetupTestResponse { Ok = ok, Detail = detail, Last = last });
    }

    // The AI step proves itself the same way: one tiny round-trip through
    // the saved provider, key, and model. It spends one call and counts
    // it against the daily ceiling — the ceiling is only honest if every
    // call that leaves the building is in the number.
    [HttpPost("api/settings/test-ai")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostTestAi(string? setup, [FromServices] AiOptions ai, [FromServices] SettingsService settings, [FromServices] SetupTestLog tests, [FromServices] AiQuota quota, [FromServices] AiProviderClient providerClient, [FromServices] ILogger<AiProviderClient> log, CancellationToken ct)
    {
        var id = await TestedAsync(settings, Setups.Ai, setup, ct);
        var values = await settings.SetupAsync(Setups.Ai, id, ct);
        var by = Principal.Email(User);
        async Task<IResult> Answer(bool ok, string detail) => Results.Ok(new SetupTestResponse
        {
            Ok = ok,
            Detail = detail,
            Last = await tests.RecordAsync(Setups.Ai, values, ok, detail, by, ct),
        });

        var config = await ai.ProviderConfigAsync(id, ct);
        if (config is null)
            return await Answer(false, "This setup has no API key saved — add one above and save first.");
        // The ceiling is the portal's, not the provider's: refused here,
        // the call never left, so the setup's last test is left alone.
        if (!await quota.TryConsumeAsync(ct))
        {
            var (used, limit) = await quota.StateAsync(ct);
            return Results.Ok(new SetupTestNotRunResponse
            {
                Ok = false,
                Detail = $"The daily call ceiling is reached ({used} of {limit} today) — raise it or try tomorrow.",
            });
        }
        try
        {
            var model = config.Model ?? AiProviderRequests.DefaultModel(config.Provider);
            var (system, user) = AiPrompts.Ping;
            _ = await providerClient.CompleteAsync(config.Provider, model, config.ApiKey, system, user, ct);
            return await Answer(true,
                $"{config.Provider} answered through model {model} (“{config.Setup}”). One call was counted against today's ceiling.");
        }
        catch (Exception e) when (e is AiProviderException or HttpRequestException or TaskCanceledException)
        {
            // The screen gets the readable line; the provider's own words
            // go to the log, which is where a key or a bill is diagnosed.
            log.LogWarning(e, "The AI connection test failed against {Provider}.", config.Provider);
            return await Answer(false, AiRules.FailureNote(e) + " The provider's own words are in the API log.");
        }
    }

    // The identity step proves itself without opening a session: a
    // made-up session asked for its decision answers 404 to a good key
    // and 401 to a bad one, so nothing is billed and nobody is verified.
    [HttpPost("api/settings/test-identity")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostTestIdentity(string? setup, [FromServices] IdentityService identity, [FromServices] SettingsService settings, [FromServices] SetupTestLog tests, CancellationToken ct)
    {
        var id = await TestedAsync(settings, Setups.Identity, setup, ct);
        var values = await settings.SetupAsync(Setups.Identity, id, ct);
        var (ok, detail) = await identity.TestAsync(id, ct);
        var last = await tests.RecordAsync(Setups.Identity, values, ok, detail, Principal.Email(User), ct);
        return Results.Ok(new SetupTestResponse { Ok = ok, Detail = detail, Last = last });
    }

    // The build host proves itself by its health: Docker, Compose and
    // free disk come back to a good token, 401 to a bad one; nothing is
    // built, so the test costs nothing to press.
    [HttpPost("api/settings/test-preview")]
    [Authorize(Policy = "admin")]
    public async Task<IResult> PostTestPreview(string? setup, [FromServices] PreviewHostClient host, [FromServices] SettingsService settings, [FromServices] SetupTestLog tests, CancellationToken ct)
    {
        var id = await TestedAsync(settings, Setups.Preview, setup, ct);
        var values = await settings.SetupAsync(Setups.Preview, id, ct);
        var (ok, detail) = await host.TestAsync(id, ct);
        var last = await tests.RecordAsync(Setups.Preview, values, ok, detail, Principal.Email(User), ct);
        return Results.Ok(new SetupTestResponse { Ok = ok, Detail = detail, Last = last });
    }

    /// <summary>The setup a test names — or, when it names none, the active one, then the main one.</summary>
    private static async Task<string> TestedAsync(SettingsService settings, SetupKind kind, string? setup, CancellationToken ct) =>
        Setups.IsValidId(setup)
            ? setup!
            : (await settings.ListAsync(kind, ct)).FirstOrDefault(s => s.Enabled)?.Id ?? Setups.MainId;
}

public sealed record TestPhoneRequest(string? To);

/// <summary>An uploaded logo or tab icon: the address it is served at, its sniffed type, and its size.</summary>
public sealed record BrandImageResponse
{
    public required string Url { get; init; }
    public required string ContentType { get; init; }
    public required int Bytes { get; init; }
}

/// <summary>A connection test that ran: whether it passed, what it said, and the test as the setup's card now keeps it.</summary>
public sealed record SetupTestResponse
{
    public required bool Ok { get; init; }
    public required string Detail { get; init; }
    public required SetupTestDto? Last { get; init; }
}

/// <summary>A connection test that never left the portal, so the setup's last test stands: no <c>last</c> at all.</summary>
public sealed record SetupTestNotRunResponse
{
    public required bool Ok { get; init; }
    public required string Detail { get; init; }
}
