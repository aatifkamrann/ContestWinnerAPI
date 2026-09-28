using System.Text.Json;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// Cloudflare Turnstile verification for the two doors bots walk through:
/// registration and opportunity entry. Dormant until both limits.captchaSiteKey
/// and limits.captchaSecret are set, so a fresh deployment asks nobody to
/// prove anything. Verification failures fail closed — during a Turnstile
/// outage nobody signs up, which for these two forms is the cheaper failure
/// than a spam wave that open entry cannot absorb.
/// </summary>
public static class Captcha
{
    public const string HttpClientName = "captcha";
    public const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    /// <summary>Pure: the feature exists only when both halves of the key pair are present.</summary>
    public static bool Required(string? siteKey, string? secret) =>
        !string.IsNullOrWhiteSpace(siteKey) && !string.IsNullOrWhiteSpace(secret);

    public static async Task<bool> RequiredAsync(SettingsService settings, CancellationToken ct) =>
        Required(
            await settings.GetAsync("limits.captchaSiteKey", ct),
            await settings.GetAsync("limits.captchaSecret", ct));

    /// <summary>Server-side verification of a widget token. False on a missing
    /// token, a failed challenge, or an unreachable verifier — never a throw.</summary>
    public static async Task<bool> VerifyAsync(
        IHttpClientFactory httpFactory,
        SettingsService settings,
        string? token,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var secret = await settings.GetAsync("limits.captchaSecret", ct);
        if (string.IsNullOrWhiteSpace(secret)) return false;

        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(VerifyUrl, new FormUrlEncodedContent(
                new Dictionary<string, string> { ["secret"] = secret, ["response"] = token }), ct);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return json.RootElement.TryGetProperty("success", out var ok) && ok.GetBoolean();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }
}
