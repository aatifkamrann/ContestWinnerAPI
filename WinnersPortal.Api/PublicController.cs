using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WinnersPortal.Api.Middleware;
using WinnersPortal.Services.Phone;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Api;

[ApiController]
public sealed class PublicController : ControllerBase
{
    // The uploaded marks, straight from the settings cache. An SVG is a
    // document as well as an image: the policy header keeps one that is
    // opened directly from running anything, and nosniff keeps the
    // browser from second-guessing the type. One action for both slots,
    // /logo and /icon; anything else in that position is a 404.
    [HttpGet("api/public/{slot:regex(^(logo|icon)$)}")]
    public async Task<IResult> GetPublicBrandImage(string slot, SettingsService settings, CancellationToken ct)
    {
        var image = BrandImage.All.SingleOrDefault(i => i.Name == slot);
        if (image is null) return Results.NotFound();
        if (!Logo.TryParse(await settings.GetAsync(image.DataKey, ct), out var type, out var bytes))
            return Results.NotFound();
        HttpContext.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
        HttpContext.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
        return Results.Bytes(bytes, type);
    }

    // Anonymous bits the web tier needs before anyone signs in.
    [HttpGet("api/public/branding")]
    public async Task<IResult> GetPublicBranding([FromServices] SettingsService settings, [FromServices] PhoneSender phones, CancellationToken ct)
    {
        var terms = await Terms.CurrentAsync(settings, ct);
        // The uploaded mark outranks the linked one; the URL it is
        // served at carries the upload stamp, so it changes with every
        // upload and browsers may keep each one forever.
        var logoStamp = await settings.GetAsync(BrandImage.Logo.StampKey, ct);
        var logoUploaded = !string.IsNullOrEmpty(logoStamp);
        var logoUrl = logoUploaded
            ? BrandImage.Logo.PublicPath(logoStamp!)
            : await settings.GetAsync("branding.logoUrl", ct);
        // The tab icon has a slot of its own — a wordmark that reads
        // beside the portal name is a smear at 16px — and falls back to
        // the logo, so a portal that never thinks about it still has one.
        var iconStamp = await settings.GetAsync(BrandImage.Icon.StampKey, ct);
        var iconUploaded = !string.IsNullOrEmpty(iconStamp);
        var defaultDurationDays =
            int.TryParse(await settings.GetAsync("opportunity.defaultDurationDays", ct), out var days) ? days : 14;
        return Results.Ok(new BrandingResponse
        {
            PortalName = await settings.GetAsync("branding.portalName", ct),
            AccentColor = await settings.GetAsync("branding.accentColor", ct),
            LogoUrl = logoUrl,
            LogoUploaded = logoUploaded,
            IconUrl = iconUploaded ? BrandImage.Icon.PublicPath(iconStamp!) : logoUrl,
            IconUploaded = iconUploaded,
            // The site key is public by design (it goes into the widget);
            // null tells the forms not to render a challenge at all.
            CaptchaSiteKey = Captcha.Required(
                await settings.GetAsync("limits.captchaSiteKey", ct),
                await settings.GetAsync("limits.captchaSecret", ct))
                ? await settings.GetAsync("limits.captchaSiteKey", ct)
                : null,
            // The shell shows the banner; the gate does the enforcing.
            MaintenanceMode = string.Equals(
                await settings.GetAsync("limits.maintenanceMode", ct), "true",
                StringComparison.OrdinalIgnoreCase),
            // Null when the portal has no terms: the signup form then
            // asks for no acceptance.
            TermsVersion = terms.Exist ? terms.Version : null,
            // Whether there is a privacy policy to link beside them. A
            // flag rather than the text: the join form only needs to
            // know whether the second link exists.
            PrivacyPublished = Privacy.Exists(await settings.GetAsync(Privacy.MarkdownKey, ct)),
            // Whether the portal can text: the signup form's phone field
            // says where the code will come from either way.
            PhoneCodes = await phones.CanSendAsync(ct),
            // The opportunity policy's default duration, so the opportunity form
            // can say what a blank deadline will be before it is saved.
            OpportunityDefaultDurationDays = defaultDurationDays,
            // Where the browser reaches the API when that is not this
            // origin: the pages stamp it on themselves, so nothing about an
            // address is compiled into the web bundle.
            ApiUrl = Origins.Of(HttpContext).ApiOrigin,
        });
    }

    // The agreement itself, for the signup link, the acceptance prompt
    // and the /terms page. Anonymous: you read the terms before you
    // have an account.
    [HttpGet("api/public/terms")]
    public async Task<IResult> GetPublicTerms([FromServices] SettingsService settings, CancellationToken ct)
    {
        var markdown = await settings.GetAsync(Terms.MarkdownKey, ct);
        var exist = Terms.Exist(markdown);
        return Results.Ok(new TermsResponse
        {
            Version = exist ? Terms.Version(await settings.GetAsync(Terms.VersionKey, ct)) : null,
            Markdown = exist ? markdown : null,
        });
    }

    // The privacy policy, on the same terms as the agreement above and
    // for the same reason: it is linked from the join form, which is
    // read by people who do not have an account yet.
    [HttpGet("api/public/privacy")]
    public async Task<IResult> GetPublicPrivacy([FromServices] SettingsService settings, CancellationToken ct)
    {
        var markdown = await settings.GetAsync(Privacy.MarkdownKey, ct);
        return Results.Ok(new PrivacyResponse { Markdown = Privacy.Exists(markdown) ? markdown : null });
    }
}

/// <summary>What the web tier needs before anyone signs in: the portal's name and marks, and what its forms must ask.</summary>
public sealed record BrandingResponse
{
    public required string? PortalName { get; init; }
    public required string? AccentColor { get; init; }
    public required string? LogoUrl { get; init; }
    public required bool LogoUploaded { get; init; }
    public required string? IconUrl { get; init; }
    public required bool IconUploaded { get; init; }
    public required string? CaptchaSiteKey { get; init; }
    public required bool MaintenanceMode { get; init; }
    public required int? TermsVersion { get; init; }
    public required bool PrivacyPublished { get; init; }
    public required bool PhoneCodes { get; init; }
    public required int OpportunityDefaultDurationDays { get; init; }
    /// <summary>Where a page sends the browser for the API — the Branding settings' API URL — or null when it is this same origin.</summary>
    public required string? ApiUrl { get; init; }
}

/// <summary>The terms of service and their version, or two nulls when the portal has none.</summary>
public sealed record TermsResponse
{
    public required int? Version { get; init; }
    public required string? Markdown { get; init; }
}

/// <summary>The privacy policy, or null when none is published.</summary>
public sealed record PrivacyResponse
{
    public required string? Markdown { get; init; }
}
