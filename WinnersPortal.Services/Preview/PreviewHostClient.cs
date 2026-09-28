using Microsoft.Extensions.Logging;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Preview;

/// <summary>
/// The one HTTP path to the build host. Every build handed over, read
/// back or forgotten goes through here, on the named client whose calls
/// the activity log records — which is what makes "what the portal told
/// the host" a one-file audit. The connection is the active setup's
/// fields; the test button proves one setup at a time, active or not.
/// </summary>
public sealed class PreviewHostClient(IHttpClientFactory httpFactory, SettingsService settings, ILogger<PreviewHostClient> log)
{
    public const string HttpClientName = "preview";

    /// <summary>The active setup's connection, or null when none is active or the active one is incomplete.</summary>
    public async Task<PreviewHostConfig?> ActiveConfigAsync(CancellationToken ct) =>
        await settings.ActiveSetupAsync(Setups.Preview, ct) is { } active ? PreviewHost.Config(active) : null;

    /// <summary>A named setup's connection — for the test button.</summary>
    public async Task<PreviewHostConfig?> ConfigAsync(string setupId, CancellationToken ct) =>
        await settings.SetupAsync(Setups.Preview, setupId, ct) is { } setup ? PreviewHost.Config(setup) : null;

    public async Task<PreviewHost.Health> HealthAsync(PreviewHostConfig c, CancellationToken ct) =>
        PreviewHost.ParseHealth(await SendAsync(PreviewHost.HealthRequest(c), ct));

    /// <summary>Hand the host a build. An id it already holds is not a second build: the agent answers with the one it has.</summary>
    public Task StartAsync(PreviewHostConfig c, Guid id, string repoFullName, string sha, string? installationToken, CancellationToken ct) =>
        SendAsync(PreviewHost.StartBuildRequest(c, id, repoFullName, sha, installationToken), ct);

    public async Task<PreviewHost.BuildAnswer> StatusAsync(PreviewHostConfig c, Guid id, CancellationToken ct) =>
        PreviewHost.ParseBuild(await SendAsync(PreviewHost.StatusRequest(c, id), ct));

    /// <summary>The whole log as the host kept it; empty when the host has none.</summary>
    public async Task<string> LogAsync(PreviewHostConfig c, Guid id, CancellationToken ct)
    {
        try
        {
            return await SendAsync(PreviewHost.LogRequest(c, id), ct);
        }
        catch (PreviewHostException e) when (e.StatusCode == 404)
        {
            return "";
        }
    }

    /// <summary>Forget a build on the host: its state and log go, the images it built stay. Already forgotten is fine.</summary>
    public async Task DeleteAsync(PreviewHostConfig c, Guid id, CancellationToken ct)
    {
        try
        {
            await SendAsync(PreviewHost.DeleteRequest(c, id), ct);
        }
        catch (PreviewHostException e) when (e.StatusCode == 404)
        {
            // nothing to forget
        }
    }

    /// <summary>Have the host run a preview. An id it already runs is not a second run: the agent answers with the one it has.</summary>
    public Task StartRunAsync(PreviewHostConfig c, Guid id, string repoFullName, string gitRef, string? installationToken, CancellationToken ct) =>
        SendAsync(PreviewHost.StartRunRequest(c, id, repoFullName, gitRef, installationToken), ct);

    public async Task<PreviewHost.RunAnswer> RunStatusAsync(PreviewHostConfig c, Guid id, CancellationToken ct) =>
        PreviewHost.ParseRun(await SendAsync(PreviewHost.RunStatusRequest(c, id), ct));

    /// <summary>Take a preview down and forget it. Already gone is fine.</summary>
    public async Task StopRunAsync(PreviewHostConfig c, Guid id, CancellationToken ct)
    {
        try
        {
            await SendAsync(PreviewHost.StopRunRequest(c, id), ct);
        }
        catch (PreviewHostException e) when (e.StatusCode == 404)
        {
            // nothing running under that id
        }
    }

    /// <summary>
    /// The settings test: the host's health to a good token, a refusal to
    /// a bad one. Nothing is built, so the test is free to press.
    /// </summary>
    public async Task<(bool Ok, string Detail)> TestAsync(string setupId, CancellationToken ct)
    {
        var config = await ConfigAsync(setupId, ct);
        if (config is null)
            return (false, "This setup has no host address or no agent token saved — add both above and save first.");
        if (config.RunUrl is { } runUrl
            && PreviewHost.RunDomainProblem(runUrl, PreviewHost.ParseWebUrl(await settings.GetAsync(WebOrigin.WebUrlKey, ct))) is { } domain)
            return (false, domain);
        try
        {
            var health = await HealthAsync(config, ct);
            if (!health.Ok)
                return (false, $"The build host answered, but says it is not ready: {PreviewHost.HealthLine(health, config.Setup)} Check Docker on the server.");
            return PreviewHost.RunLine(health, config) is { } run
                ? (run.Ok, PreviewHost.HealthLine(health, config.Setup) + " " + run.Line)
                : (true, PreviewHost.HealthLine(health, config.Setup));
        }
        catch (PreviewHostException e)
        {
            return e.StatusCode switch
            {
                401 or 403 => (false, "The build host rejected the token — paste the one the server's install printed, or write a new one to /etc/preview-agent/token there."),
                404 => (false, "The address answered, but not as the build agent: check it names the agent's host and nothing more."),
                _ => (false, e.Message),
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(e, "The build host test could not reach {Host}.", config.Host);
            return (false, "The build host could not be reached from this server. Check the address, the server's firewall (443 open to this portal) and that the agent is running; the API log has the connection error.");
        }
    }

    private async Task<string> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        using (message)
        {
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(message, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new PreviewHostException((int)response.StatusCode, PreviewHost.ErrorDetail((int)response.StatusCode, body));
            return body;
        }
    }
}
