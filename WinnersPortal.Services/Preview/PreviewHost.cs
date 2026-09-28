using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Preview;

/// <summary>The build host's settings — one setup's fields (<see cref="Setups.Preview"/>).</summary>
public static class PreviewHostKeys
{
    public const string HostUrl = "preview.hostUrl";
    public const string Token = "preview.token";
    public const string BuildTimeoutMinutes = "preview.buildTimeoutMinutes";
    public const string RunUrl = "preview.runUrl";
    public const string IdleMinutes = "preview.idleMinutes";
    public const string MaxRunning = "preview.maxRunning";
}

/// <summary>
/// One build host as the portal calls it: where, with which token, and how
/// long a build may take; and, once it runs previews, the parent every
/// preview's address sits under, how long one may go unvisited, and how
/// many may run at once. <c>RunUrl</c> null means builds only.
/// </summary>
public sealed record PreviewHostConfig(
    Uri Host,
    string Token,
    TimeSpan BuildTimeout,
    string Setup = Setups.MainName,
    Uri? RunUrl = null,
    int IdleMinutes = PreviewHost.DefaultIdleMinutes,
    int MaxRunning = PreviewHost.DefaultMaxRunning);

/// <summary>
/// The wire between the portal and the build agent (<c>deploy/preview-host/preview-agent.py</c>),
/// kept pure so tests can pin it: a setup's fields to a connection, each
/// call as a request, each answer parsed. The agent is the portal's own,
/// so its contract is small — health, start a build, read it, read its
/// log, forget it — and every call carries the agent's token as a bearer.
/// The token, and the GitHub installation token a build is handed to
/// clone with, are marked secret for the activity log.
/// </summary>
public static class PreviewHost
{
    public const int DefaultTimeoutMinutes = 15;
    public const int MinTimeoutMinutes = 1;
    public const int MaxTimeoutMinutes = 120;

    public const int DefaultIdleMinutes = 30;
    public const int MinIdleMinutes = 5;
    public const int MaxIdleMinutes = 1440;

    public const int DefaultMaxRunning = 3;
    public const int MinMaxRunning = 1;
    public const int MaxMaxRunning = 20;

    /// <summary>What the agent's <c>/health</c> answers.</summary>
    public sealed record Health(
        bool Ok, string? Docker, string? Compose, long? DiskFreeMb, int Queued, string? Building,
        int Running = 0, string? RunDomain = null);

    /// <summary>What the agent says of one build: <c>queued</c>, <c>building</c>, <c>built</c> or <c>failed</c>, and the rest once it ran.</summary>
    public sealed record BuildAnswer(
        string Status, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int? ExitCode, string? Error, string? LogTail);

    public const string Queued = "queued";
    public const string Building = "building";
    public const string Built = "built";
    public const string Failed = "failed";

    /// <summary>What the agent says of one preview: <c>queued</c>, <c>starting</c>, <c>running</c>, <c>stopped</c> or <c>failed</c>.</summary>
    public sealed record RunAnswer(
        string Status, string? Sha, DateTimeOffset? StartedAt, DateTimeOffset? LastSeenAt, DateTimeOffset? StoppedAt,
        string? StopReason, string? Error, string? LogTail, string? Notes);

    public const string Starting = "starting";
    public const string Running = "running";
    public const string Stopped = "stopped";

    // ------------------------------------------------------------ settings

    /// <summary>Pure: a setup's fields to a connection, or null while it has no address or no token.</summary>
    public static PreviewHostConfig? Config(SetupValues s)
    {
        var token = s.Get(PreviewHostKeys.Token);
        if (string.IsNullOrWhiteSpace(token) || ParseHost(s.Get(PreviewHostKeys.HostUrl)) is not { } host) return null;
        var minutes = ParseTimeoutMinutes(s.Get(PreviewHostKeys.BuildTimeoutMinutes)) ?? DefaultTimeoutMinutes;
        return new PreviewHostConfig(
            host, token.Trim(), TimeSpan.FromMinutes(minutes), s.Name,
            ParseRunUrl(s.Get(PreviewHostKeys.RunUrl)),
            ParseIdleMinutes(s.Get(PreviewHostKeys.IdleMinutes)) ?? DefaultIdleMinutes,
            ParseMaxRunning(s.Get(PreviewHostKeys.MaxRunning)) ?? DefaultMaxRunning);
    }

    /// <summary>An absolute http(s) address with nothing after the path, ending in a slash so relative calls append to it.</summary>
    public static Uri? ParseHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        return uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri.GetLeftPart(UriPartial.Path) + "/");
    }

    /// <summary>A whole number of minutes within the range, or null.</summary>
    public static int? ParseTimeoutMinutes(string? value) => Whole(value, MinTimeoutMinutes, MaxTimeoutMinutes);

    public static int? ParseIdleMinutes(string? value) => Whole(value, MinIdleMinutes, MaxIdleMinutes);

    public static int? ParseMaxRunning(string? value) => Whole(value, MinMaxRunning, MaxMaxRunning);

    private static int? Whole(string? value, int min, int max) =>
        int.TryParse(value?.Trim(), out var n) && n >= min && n <= max ? n : null;

    /// <summary>
    /// The parent every preview's address sits under: an http(s) origin
    /// naming a host (not an IP address: a label goes in front of it), a
    /// port if it needs one, and nothing else. <c>https://previews.example.net</c>.
    /// </summary>
    public static Uri? ParseRunUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.HostNameType != UriHostNameType.Dns) return null;
        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    /// <summary>What the host calls a preview, and the first label of its address: <c>p-</c> and the id's first eight hex digits.</summary>
    public static string Label(Guid id) => "p-" + id.ToString("N")[..8];

    /// <summary>The preview's own address: its label in front of the parent, with the parent's scheme and port.</summary>
    public static Uri Address(Uri runUrl, Guid id) =>
        new UriBuilder(runUrl.Scheme, Label(id) + "." + runUrl.Host, runUrl.IsDefaultPort ? -1 : runUrl.Port, "/").Uri;

    /// <summary>
    /// Why previews may not live under this parent, or null. They must sit
    /// outside the portal's own domain: in the two-site layout the portal's
    /// sign-in cookie is set for that whole domain and would be sent to the
    /// entrant's code, and on any layout a page under it could plant a
    /// cookie the portal then reads. <c>webUrl</c> is the portal's Web URL.
    /// </summary>
    public static string? RunDomainProblem(Uri runUrl, Uri? webUrl)
    {
        if (webUrl is null) return null;
        var run = runUrl.Host.ToLowerInvariant();
        var web = webUrl.Host.ToLowerInvariant();
        var parent = WebOrigin.DomainOf(webUrl)?.ToLowerInvariant();
        static bool Under(string host, string domain) =>
            host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        if (Under(run, web) || (parent is not null && Under(run, parent)))
            return $"Previews must live outside the portal’s own domain ({parent ?? web}): a preview there would be sent "
                   + "the portal’s sign-in cookie, or could plant one the portal reads. Give them a domain of their own, "
                   + "e.g. https://previews.example.net.";
        return null;
    }

    /// <summary>The portal's Web URL setting as an address, or null while it is blank or unusable.</summary>
    public static Uri? ParseWebUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;

    /// <summary>
    /// The settings test's second line, once a preview address is set: does
    /// the host serve previews under the same name the setting gives them?
    /// Null where the setup has no preview address.
    /// </summary>
    public static (bool Ok, string Line)? RunLine(Health h, PreviewHostConfig c)
    {
        if (c.RunUrl is not { } runUrl) return null;
        var host = runUrl.Host;
        if (string.IsNullOrWhiteSpace(h.RunDomain))
            return (false, $"It serves no previews yet: set PREVIEW_RUN_DOMAIN={host} in /etc/preview-agent/env on the server and restart the agent.");
        if (!string.Equals(h.RunDomain.Trim().TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase))
            return (false, $"It serves previews under {h.RunDomain}, but the preview address here names {host}: make the two the same.");
        return (true, $"Previews open at {Address(runUrl, Guid.Empty).GetLeftPart(UriPartial.Authority).Replace("p-00000000", "p-…")}.");
    }

    /// <summary>Why a value may not be saved under one of the host's keys, or null. Hooked into the settings screen's checks.</summary>
    public static string? Problem(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var main = SettingsRegistry.MainKey(key);
        if (main == PreviewHostKeys.HostUrl && ParseHost(value) is null)
            return "The build host address must be a full http:// or https:// address with nothing after the host, e.g. https://build.example.com.";
        if (main == PreviewHostKeys.BuildTimeoutMinutes && ParseTimeoutMinutes(value) is null)
            return $"Build timeout must be a whole number of minutes from {MinTimeoutMinutes} to {MaxTimeoutMinutes}.";
        if (main == PreviewHostKeys.RunUrl && ParseRunUrl(value) is null)
            return "The preview address must be http:// or https:// and a host name with nothing after it (a port is fine), e.g. https://previews.example.net.";
        if (main == PreviewHostKeys.IdleMinutes && ParseIdleMinutes(value) is null)
            return $"Idle stop must be a whole number of minutes from {MinIdleMinutes} to {MaxIdleMinutes}.";
        if (main == PreviewHostKeys.MaxRunning && ParseMaxRunning(value) is null)
            return $"Previews running at once must be a whole number from {MinMaxRunning} to {MaxMaxRunning}.";
        return null;
    }

    // ------------------------------------------------------------ requests

    public static HttpRequestMessage HealthRequest(PreviewHostConfig c) => Request(c, HttpMethod.Get, "health");

    /// <summary>
    /// Hand the host a build: the checkpoint's id names it on both sides,
    /// the repository and commit say what to build, and the installation
    /// token — short-lived, read-only — lets the host clone a private
    /// repository. Null token for a public one.
    /// </summary>
    public static HttpRequestMessage StartBuildRequest(
        PreviewHostConfig c, Guid id, string repoFullName, string sha, string? installationToken)
    {
        var message = Request(c, HttpMethod.Post, "builds");
        message.Content = new StringContent(JsonSerializer.Serialize(new
        {
            id = id.ToString(),
            repo = repoFullName,
            sha,
            token = installationToken,
            timeoutSeconds = (int)c.BuildTimeout.TotalSeconds,
        }), Encoding.UTF8, "application/json");
        return message.WithSecrets(installationToken);
    }

    public static HttpRequestMessage StatusRequest(PreviewHostConfig c, Guid id) => Request(c, HttpMethod.Get, $"builds/{id}");

    /// <summary>
    /// Have the host run a preview: the preview's id names it (and, by its
    /// first eight hex digits, its address and the compose project a build
    /// of the same checkpoint left images under), the repository and ref say
    /// what to check out — a claim's commit, or <c>final</c> — and the
    /// installation token clones a private repository. The idle stop and the
    /// start timeout are the setup's, sent with every run.
    /// </summary>
    public static HttpRequestMessage StartRunRequest(
        PreviewHostConfig c, Guid id, string repoFullName, string gitRef, string? installationToken)
    {
        var message = Request(c, HttpMethod.Post, "runs");
        message.Content = new StringContent(JsonSerializer.Serialize(new
        {
            id = id.ToString(),
            repo = repoFullName,
            @ref = gitRef,
            token = installationToken,
            idleSeconds = c.IdleMinutes * 60,
            timeoutSeconds = (int)c.BuildTimeout.TotalSeconds,
        }), Encoding.UTF8, "application/json");
        return message.WithSecrets(installationToken);
    }

    public static HttpRequestMessage RunStatusRequest(PreviewHostConfig c, Guid id) => Request(c, HttpMethod.Get, $"runs/{id}");

    /// <summary>Take a preview down (containers, networks, volumes; the images stay) and forget it.</summary>
    public static HttpRequestMessage StopRunRequest(PreviewHostConfig c, Guid id) => Request(c, HttpMethod.Delete, $"runs/{id}");

    public static HttpRequestMessage LogRequest(PreviewHostConfig c, Guid id) => Request(c, HttpMethod.Get, $"builds/{id}/log");

    public static HttpRequestMessage DeleteRequest(PreviewHostConfig c, Guid id) => Request(c, HttpMethod.Delete, $"builds/{id}");

    private static HttpRequestMessage Request(PreviewHostConfig c, HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(c.Host, path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Token);
        return message.WithSecrets(c.Token);
    }

    // ------------------------------------------------------------- answers

    public static Health ParseHealth(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new Health(
            root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
            Text(root, "docker"),
            Text(root, "compose"),
            Number(root, "diskFreeMb"),
            (int?)Number(root, "queued") ?? 0,
            Text(root, "building"),
            (int?)Number(root, "running") ?? 0,
            Text(root, "runDomain"));
    }

    public static BuildAnswer ParseBuild(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = Text(root, "status")
            ?? throw new PreviewHostException(0, "The build host's answer named no status.");
        return new BuildAnswer(
            status,
            Time(root, "startedAt"),
            Time(root, "finishedAt"),
            (int?)Number(root, "exitCode"),
            Text(root, "error"),
            Text(root, "logTail"));
    }

    public static RunAnswer ParseRun(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = Text(root, "status")
            ?? throw new PreviewHostException(0, "The build host's answer named no status.");
        return new RunAnswer(
            status,
            Text(root, "sha"),
            Time(root, "startedAt"),
            Time(root, "lastSeenAt"),
            Time(root, "stoppedAt"),
            Text(root, "stopReason"),
            Text(root, "error"),
            Text(root, "logTail"),
            Text(root, "notes"));
    }

    /// <summary>The settings test's line: what the host is running, in words an administrator can check against the server.</summary>
    public static string HealthLine(Health h, string setup)
    {
        var disk = h.DiskFreeMb is { } mb ? $"{mb / 1024.0:0.#} GB free" : "free disk unknown";
        var doing = h.Building is not null ? "a build is running" : h.Queued > 0 ? $"{h.Queued} queued" : "idle";
        var running = h.Running > 0 ? $", {h.Running} preview{(h.Running == 1 ? "" : "s")} running" : "";
        return $"The build host answered (“{setup}”): Docker {h.Docker ?? "?"}, Compose {h.Compose ?? "?"}, {disk}, {doing}{running}.";
    }

    /// <summary>The agent's error body is {"error":…}; dig it out for a readable note.</summary>
    public static string ErrorDetail(int statusCode, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (Text(doc.RootElement, "error") is { } msg)
                return $"The build host answered {statusCode}: {(msg.Length > 300 ? msg[..300] : msg)}";
        }
        catch (JsonException)
        {
            // fall through to the generic line
        }
        return $"The build host answered {statusCode}.";
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A number, or null for a missing or null field — TryGetInt32 throws on JSON null.</summary>
    private static long? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Text(e, name) is { } s && DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

/// <summary>A refusal from the build host, with the status it came with; 0 is the portal's own refusal.</summary>
public sealed class PreviewHostException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
