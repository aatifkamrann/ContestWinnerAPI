using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.GitHub;

/// <summary>
/// Everything that talks to GitHub, as the App. Authentication is the
/// two-step the platform requires — a short-lived RS256 JWT proves the app,
/// then an installation token (cached until shortly before expiry) acts on
/// the opportunity organisation. Configuration lives in settings, so an admin can
/// stand the integration up without a redeploy; every method throws
/// <see cref="GitHubApiException"/> with GitHub's own message on failure.
///
/// <para>A portal can hold several Apps, each on its own organization, one
/// of them active (Settings/Setups.cs). Every call names its owner in its
/// path, and that picks the setup: a repository is reached through the
/// setup for its organization — active or not, since making another App
/// active stops new repositories going there rather than orphaning the ones
/// already in it — and a new one is made through the active setup.</para>
///
/// <para>One call cannot go as the App: GitHub keeps repository transfers
/// off the installation token's list of endpoints, whatever permissions the
/// App holds, and answers them "Resource not accessible by integration". A
/// winner's handover therefore goes out on the setup's transfer token — a
/// classic personal access token from an owner of the organization — and
/// on nothing else.</para>
/// </summary>
public sealed class GitHubService(
    SettingsService settings,
    IHttpClientFactory httpFactory,
    ILogger<GitHubService> log)
{
    public const string HttpClientName = "github";

    /// <summary>Archive downloads run minutes, not the API client's 30 seconds.</summary>
    public const string ZipballHttpClientName = "github-zipball";

    /// <summary>The setup field holding the owner's classic token that repository transfers go out on.</summary>
    public const string TransferTokenKey = "github.transferToken";

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokens = new(StringComparer.Ordinal);

    /// <summary>The active setup has the app credentials and org. Nothing GitHub-side runs without them.</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default) =>
        await settings.ActiveSetupAsync(Setups.GitHub, ct) is { } active && AppReady(active);

    /// <summary>Pure: the three values the App side of a setup cannot act without.</summary>
    public static bool AppReady(SetupValues s) =>
        !string.IsNullOrWhiteSpace(s.Get("github.appId"))
        && !string.IsNullOrWhiteSpace(s.Get("github.privateKey"))
        && !string.IsNullOrWhiteSpace(s.Get("github.organization"));

    /// <summary>Pure: the pair "Connect GitHub" signs people in with.</summary>
    public static bool ClientReady(SetupValues s) =>
        !string.IsNullOrWhiteSpace(s.Get("github.clientId"))
        && !string.IsNullOrWhiteSpace(s.Get("github.clientSecret"));

    /// <summary>
    /// Pure: the setup a call about <paramref name="owner"/>'s repositories
    /// goes through — a complete setup for that organization, preferring the
    /// active one; otherwise (a repository since handed to its client) the
    /// active setup, when it is complete. Null when there is none.
    /// </summary>
    public static SetupValues? Route(IEnumerable<SetupValues> setups, string? owner)
    {
        var ready = setups.Where(AppReady).ToList();
        if (!string.IsNullOrEmpty(owner)
            && ready.Where(s => string.Equals(s.Get("github.organization")?.Trim(), owner, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Enabled)
                .FirstOrDefault() is { } match)
            return match;
        return ready.FirstOrDefault(s => s.Enabled);
    }

    /// <summary>Pure: the organization or user a REST path is about — <c>/repos/{owner}/…</c> or <c>/orgs/{org}/…</c>.</summary>
    public static string? OwnerOf(string path)
    {
        var parts = path.Split('/', 4, StringSplitOptions.None);
        return parts.Length >= 3 && parts[0].Length == 0 && parts[1] is "repos" or "orgs" && parts[2].Length > 0
            ? Uri.UnescapeDataString(parts[2])
            : null;
    }


    /// <summary>
    /// Whether "Connect GitHub" can finish here. Deliberately not
    /// <see cref="IsConfiguredAsync"/>: the App trio builds repositories, but
    /// the round trip through GitHub's consent page is the OAuth pair, and a
    /// portal can hold one without the other. Gating the button on the wrong
    /// three is how you get a button that bounces back with ?github=unconfigured.
    /// </summary>
    public async Task<bool> CanConnectAsync(CancellationToken ct = default) =>
        await ConnectSetupAsync(ct) is not null;

    /// <summary>The setup "Connect GitHub" goes through: the active one, once it has its client pair.</summary>
    public async Task<SetupValues?> ConnectSetupAsync(CancellationToken ct = default) =>
        await settings.ActiveSetupAsync(Setups.GitHub, ct) is { } active && ClientReady(active) ? active : null;

    /// <summary>
    /// Pure: why a transfer token cannot hand a repository over, judged
    /// before GitHub is asked, or null when it may. The message continues a
    /// sentence, so it starts in lower case.
    /// </summary>
    public static string? TransferTokenProblem(string? token, string org)
    {
        var t = token?.Trim() ?? "";
        if (t.Length == 0)
            return "no transfer token is saved. GitHub does not let an App transfer a repository, so the portal "
                + $"needs a classic personal access token with the repo scope from an owner of {org}.";
        if (t.StartsWith("github_pat_", StringComparison.Ordinal))
            return "the transfer token is a fine-grained token, and GitHub refuses repository transfers from those. "
                + "Create a classic token (it starts ghp_) with the repo scope.";
        if (t.StartsWith("ghs_", StringComparison.Ordinal) || t.StartsWith("ghu_", StringComparison.Ordinal)
            || t.StartsWith("ghr_", StringComparison.Ordinal))
            return "the transfer token is an app token, not a personal access token. Create a classic token "
                + $"(it starts ghp_) with the repo scope, signed in as an owner of {org}.";
        return null;
    }

    /// <summary>What one re-read of a repository says about a transfer the portal requested.</summary>
    public enum TransferSight
    {
        /// <summary>Still with its old owner: the recipient has not accepted yet.</summary>
        Pending,
        /// <summary>Seen under the new owner.</summary>
        Arrived,
        /// <summary>Gone from the organization, and nowhere the portal can read.</summary>
        Left,
    }

    /// <summary>
    /// Pure: a transfer's state from a re-read — null is the 404 an App gets
    /// once the repository sits in a personal account it is not installed on.
    /// The portal never deletes an entry repository, so after a requested
    /// transfer that absence is the transfer landing.
    /// </summary>
    public static TransferSight ReadTransfer(GitHubRepo? repo, string target) =>
        repo is null ? TransferSight.Left
        : string.Equals(repo.OwnerLogin, target, StringComparison.OrdinalIgnoreCase) ? TransferSight.Arrived
        : TransferSight.Pending;

    /// <summary>
    /// The blueprint's ten-seconds-versus-two-days trade: prove the whole
    /// credential chain — PEM parse, app JWT, installation on the org, repo
    /// administration — by creating, reading back, and deleting a scratch
    /// repository. A mis-pasted private key should fail here, on this
    /// button, not two days later at the first entrant's missing repo.
    /// The transfer token is proved against the same scratch repository,
    /// since a setup that cannot hand a winner over fails at the worst
    /// moment there is: after the client has paid.
    /// </summary>
    public async Task<(bool Ok, string Detail)> TestAsync(string setupId, CancellationToken ct)
    {
        var setup = await settings.SetupAsync(Setups.GitHub, setupId, ct);
        if (setup is null)
            return (false, "That GitHub setup is not in the list any more — reload the settings.");
        if (!AppReady(setup))
            return (false, "This setup is not complete — app ID, private key, and organization are all required.");

        GitHubRepo created;
        try
        {
            created = await CreateOrgRepoAsync(setup,
                $"health-roundtrip-{Guid.NewGuid():N}",
                "Winners Portal self-test. Created and deleted by the settings test button; safe to delete.", ct);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            // RSA.ImportFromPem on a mangled paste — the exact failure this
            // button exists to catch early.
            return (false, "The private key could not be parsed. Re-paste the whole PEM from GitHub's "
                + ".pem download, including the BEGIN and END lines.");
        }
        catch (GitHubApiException e)
        {
            return (false, e.StatusCode == 403
                ? $"{e.Message} — the app's repository permissions must include Administration: read & write."
                : e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (false, e.Message);
        }

        // From here every failure must name the leftover — a test that
        // strands junk repositories in the organization is worse than one
        // that fails loudly.
        try
        {
            var readBack = await GetRepoAsync(created.FullName, ct, via: setup);
            var (transferProblem, transferLogin) = readBack is null
                ? (null, null)
                : await CheckTransferTokenAsync(setup, created.FullName, ct);
            using (var _ = await SendAsync(HttpMethod.Delete, $"/repos/{created.FullName}", null, ct, via: setup)) { }
            if (readBack is null)
                return (false, $"Created '{created.FullName}' but could not read it back before deleting it — "
                    + "is something renaming or removing repositories in the organization?");
            return transferProblem is not null
                ? (false, $"The app works — it created, read back, and deleted scratch repository '{created.FullName}' — "
                    + $"but a paid winner's repository could not be handed over: {transferProblem}")
                : (true, $"Created, read back, and deleted scratch repository '{created.FullName}'. "
                    + $"The app can administer repositories in the organization, and the transfer token "
                    + $"(@{transferLogin}) can hand a winner's repository over.");
        }
        catch (Exception e) when (e is GitHubApiException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(e, "GitHub round-trip failed after creating {Repo}.", created.FullName);
            return (false, $"{e.Message} — the scratch repository '{created.FullName}' may still exist; "
                + "delete it from the organization by hand.");
        }
    }

    // ------------------------------------------------------------ repos

    /// <summary>A private repository in the active setup's organization.</summary>
    public async Task<GitHubRepo> CreateOrgRepoAsync(string name, string description, CancellationToken ct) =>
        await settings.ActiveSetupAsync(Setups.GitHub, ct) is { } active && AppReady(active)
            ? await CreateOrgRepoAsync(active, name, description, ct)
            : throw new GitHubApiException(0, "No GitHub setup is active and complete (app ID, private key, organization).");

    private async Task<GitHubRepo> CreateOrgRepoAsync(SetupValues setup, string name, string description, CancellationToken ct)
    {
        var org = setup.Get("github.organization")!.Trim();
        using var doc = await SendAsync(HttpMethod.Post, $"/orgs/{Uri.EscapeDataString(org)}/repos", new
        {
            name,
            description,
            @private = true,
            has_issues = true,
            has_projects = false,
            has_wiki = false,
            auto_init = false, // the first contents PUT creates the default branch
        }, ct, via: setup);
        return ReadRepo(doc.RootElement);
    }

    // Deletion exists for exactly one caller, the settings test's scratch
    // repository, and goes inline there. Entry repositories are archived or
    // transferred, never deleted — losing work must survive.

    public async Task<GitHubRepo?> GetRepoAsync(string fullName, CancellationToken ct, SetupValues? via = null)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Get, $"/repos/{fullName}", null, ct, via);
            return ReadRepo(doc.RootElement);
        }
        catch (GitHubApiException e) when (e.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task PutFileAsync(string fullName, string path, string content, string message, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Put, $"/repos/{fullName}/contents/{path}", new
        {
            message,
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
        }, ct);
    }

    /// <summary>Invites (or re-permissions) a collaborator: "push" for entrants, "pull" for review and freeze.</summary>
    public async Task SetCollaboratorAsync(string fullName, string username, string permission, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Put,
            $"/repos/{fullName}/collaborators/{Uri.EscapeDataString(username)}",
            new { permission }, ct);
    }

    /// <summary>
    /// Takes a collaborator off a repository altogether — and, for someone
    /// who never accepted, cancels the invitation, which is the same call to
    /// GitHub. Used when an entrant is removed from an opportunity and their clone
    /// window has run out; a 404 means they were already gone.
    /// </summary>
    public async Task RemoveCollaboratorAsync(string fullName, string username, CancellationToken ct)
    {
        try
        {
            using var _ = await SendAsync(HttpMethod.Delete,
                $"/repos/{fullName}/collaborators/{Uri.EscapeDataString(username)}", null, ct);
        }
        catch (GitHubApiException e) when (e.StatusCode == 404)
        {
            // Already off the repository, or the repository itself is gone.
        }
    }

    public async Task ArchiveRepoAsync(string fullName, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Patch, $"/repos/{fullName}", new { archived = true }, ct);
    }

    /// <summary>
    /// Why a transfer of <paramref name="fullName"/> cannot be fired yet — no
    /// setup, or a transfer token that cannot work — as the note an operator
    /// reads, or null when it can. Asks nothing of GitHub, so the worker can
    /// check it every sweep and start the transfer the moment it is fixed.
    /// </summary>
    public async Task<string?> TransferBlockerAsync(string fullName, CancellationToken ct)
    {
        var setup = Route(await settings.SetupsAsync(Setups.GitHub, ct), OwnerOf($"/repos/{fullName}"));
        if (setup is null)
            return "Transfer not started: no complete GitHub setup answers for this repository's organization.";
        var org = setup.Get("github.organization")!.Trim();
        return TransferTokenProblem(setup.Get(TransferTokenKey), org) is { } problem
            ? $"Transfer not started: {problem} Fix it under Settings, GitHub (“{setup.Name}”); "
                + "the transfer starts by itself on the sweep after."
            : null;
    }

    /// <summary>
    /// Fires the transfer, on the setup's transfer token rather than as the
    /// App (see the class summary). A 202 is not the handover — the worker
    /// re-reads the repo to verify.
    /// </summary>
    public async Task TransferRepoAsync(string fullName, string newOwner, CancellationToken ct)
    {
        var path = $"/repos/{fullName}/transfer";
        var setup = await RouteAsync(path, ct);
        var org = setup.Get("github.organization")!.Trim();
        var token = setup.Get(TransferTokenKey)?.Trim();
        if (TransferTokenProblem(token, org) is { } problem)
            throw new GitHubApiException(0, $"Transfer not started: {problem}");
        try
        {
            using var _ = await SendAsync(HttpMethod.Post, path, new { new_owner = newOwner }, ct, via: setup, token: token);
        }
        catch (GitHubApiException e) when (e.StatusCode == 401)
        {
            throw new GitHubApiException(401, $"GitHub refused the transfer token as bad credentials — it has expired "
                + "or been revoked. Paste a new classic token under Settings, GitHub.");
        }
        catch (GitHubApiException e) when (e.StatusCode is 403 or 404)
        {
            throw new GitHubApiException(e.StatusCode, $"{Unprefixed(e)} — the transfer token must belong to an owner "
                + $"of {org} and carry the repo scope.");
        }
    }

    /// <summary>
    /// A repository the App can no longer see, looked for by its id through
    /// the transfer token of the setup it left, in case that account can
    /// still read it where it went. Null when it cannot — the usual answer
    /// for a private repository now in somebody's personal account.
    /// </summary>
    public async Task<GitHubRepo?> FindMovedRepoAsync(string formerFullName, long repoId, CancellationToken ct)
    {
        var setup = await RouteAsync($"/repos/{formerFullName}", ct);
        var token = setup.Get(TransferTokenKey)?.Trim();
        if (TransferTokenProblem(token, "") is not null) return null;
        using var response = await SendRawAsync(HttpMethod.Get, $"/repositories/{repoId}", null, token!, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ReadRepo(doc.RootElement);
    }

    /// <summary>
    /// Proves a setup's transfer token against a repository the App just
    /// made: GitHub takes it, it carries the repo scope, and its account has
    /// admin rights there — which on a repository the App created means an
    /// owner of the organization. Never throws, so the caller still deletes
    /// the scratch repository; returns the problem, or the token's login.
    /// </summary>
    private async Task<(string? Problem, string? Login)> CheckTransferTokenAsync(
        SetupValues setup, string scratchFullName, CancellationToken ct)
    {
        var org = setup.Get("github.organization")!.Trim();
        var token = setup.Get(TransferTokenKey)?.Trim();
        if (TransferTokenProblem(token, org) is { } problem) return (problem, null);
        try
        {
            string login;
            using (var who = await SendRawAsync(HttpMethod.Get, "/user", null, token!, ct))
            {
                if (who.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return ("GitHub refused the transfer token as bad credentials — it has expired or been revoked, "
                        + "or the paste is incomplete.", null);
                if (!who.IsSuccessStatusCode)
                    return ($"GitHub answered {(int)who.StatusCode} to the transfer token: "
                        + $"{await ErrorMessageAsync(who, ct)}", null);
                using var user = JsonDocument.Parse(await who.Content.ReadAsStringAsync(ct));
                login = user.RootElement.GetProperty("login").GetString() ?? "?";
                // Classic tokens always answer with their scopes, even none.
                if (who.Headers.TryGetValues("X-OAuth-Scopes", out var scopes)
                    && !string.Join(',', scopes).Split(',', StringSplitOptions.TrimEntries).Contains("repo"))
                    return ($"the transfer token (@{login}) lacks the repo scope — create it again with repo ticked.", login);
            }

            using var repo = await SendRawAsync(HttpMethod.Get, $"/repos/{scratchFullName}", null, token!, ct);
            if (repo.StatusCode == System.Net.HttpStatusCode.NotFound)
                return ($"@{login}, the transfer token's account, cannot see private repositories in {org}. "
                    + $"The token must belong to an owner of {org}.", login);
            if (!repo.IsSuccessStatusCode)
                return ($"GitHub refused @{login}'s transfer token on {org}: {await ErrorMessageAsync(repo, ct)}", login);
            using var body = JsonDocument.Parse(await repo.Content.ReadAsStringAsync(ct));
            var admin = body.RootElement.TryGetProperty("permissions", out var permissions)
                && permissions.TryGetProperty("admin", out var a) && a.GetBoolean();
            return admin
                ? (null, login)
                : ($"@{login}, the transfer token's account, has no admin rights on {org}'s repositories, and GitHub "
                    + $"transfers a repository only for someone who does. Use a token from an owner of {org}.", login);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ($"the transfer token could not be checked: {e.Message}", null);
        }
    }

    /// <summary>Tags the current head of the default branch. No-op on an empty repo or an existing tag.</summary>
    public async Task<bool> CreateTagAsync(string fullName, string tag, CancellationToken ct)
    {
        var repo = await GetRepoAsync(fullName, ct);
        if (repo is null) return false;
        string sha;
        try
        {
            using var head = await SendAsync(HttpMethod.Get,
                $"/repos/{fullName}/git/ref/heads/{Uri.EscapeDataString(repo.DefaultBranch)}", null, ct);
            sha = head.RootElement.GetProperty("object").GetProperty("sha").GetString()!;
        }
        catch (GitHubApiException e) when (e.StatusCode == 404)
        {
            return false; // an empty repo has no head to tag — nothing was ever pushed
        }
        try
        {
            using var _ = await SendAsync(HttpMethod.Post, $"/repos/{fullName}/git/refs",
                new { @ref = $"refs/tags/{tag}", sha }, ct);
            return true;
        }
        catch (GitHubApiException e) when (e.StatusCode is 409 or 422)
        {
            return true; // the tag already exists; the freeze is idempotent
        }
    }

    /// <summary>
    /// Streams a repository archive into <paramref name="destination"/> and
    /// returns its size. GitHub answers with a redirect to codeload carrying
    /// its own token, which the default handler follows (dropping our
    /// Authorization header across hosts, as it should). 404 = no such ref.
    /// </summary>
    public async Task<long> DownloadZipballAsync(
        string fullName, string gitRef, Stream destination, long maxBytes, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ZipballHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{fullName}/zipball/{gitRef}");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", await GetInstallationTokenAsync(await RouteAsync($"/repos/{fullName}", ct), ct));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new GitHubApiException((int)response.StatusCode,
                $"GET zipball {fullName}@{gitRef}: {Truncate(await response.Content.ReadAsStringAsync(ct))}");

        // No trustworthy Content-Length on archives — count while copying.
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new GitHubApiException(0,
                    $"the archive passed {maxBytes / (1024 * 1024)} MB — too big to package; review it on GitHub.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }

    // ------------------------------------------- read-only repository facts
    // Consumed by the AI layer's input builders. All of these read; none of
    // them write — a digest or a spam scan must never touch an entrant's repo.

    /// <summary>Bytes per language, GitHub's own detection. Empty for an empty repo.</summary>
    public async Task<List<string>> GetLanguagesAsync(string fullName, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"/repos/{fullName}/languages", null, ct);
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    /// <summary>The commit a ref points at — "tags/final", "heads/main" — or null when it does not exist.</summary>
    public async Task<string?> GetRefShaAsync(string fullName, string gitRef, CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Get, $"/repos/{fullName}/git/ref/{gitRef}", null, ct);
            return doc.RootElement.GetProperty("object").GetProperty("sha").GetString();
        }
        catch (GitHubApiException e) when (e.StatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>Every file path at a commit, capped. The listing, never the contents.</summary>
    public async Task<List<string>> GetTreePathsAsync(string fullName, string sha, int max, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"/repos/{fullName}/git/trees/{Uri.EscapeDataString(sha)}?recursive=1", null, ct);
        var paths = new List<string>();
        foreach (var node in doc.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (node.TryGetProperty("type", out var type) && type.GetString() == "blob"
                && node.TryGetProperty("path", out var path) && path.GetString() is { } p)
            {
                paths.Add(p);
                if (paths.Count >= max) break;
            }
        }
        return paths;
    }

    /// <summary>The repository's README as text, clipped; null when there is none.</summary>
    public async Task<string?> GetReadmeTextAsync(string fullName, string gitRef, int maxChars, CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Get,
                $"/repos/{fullName}/readme?ref={Uri.EscapeDataString(gitRef)}", null, ct);
            if (!doc.RootElement.TryGetProperty("content", out var content)) return null;
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(
                content.GetString()!.Replace("\n", "")));
            return text.Length <= maxChars ? text : text[..maxChars];
        }
        catch (GitHubApiException e) when (e.StatusCode == 404)
        {
            return null;
        }
    }

    // ---------------------------------------------------- user OAuth flow

    /// <summary>
    /// Exchanges an authorisation code from the App's user flow for the
    /// account's id and login — through the setup the flow started with, since
    /// a code only redeems against the client that asked for it.
    /// </summary>
    public async Task<(long Id, string Login)> ExchangeUserCodeAsync(string code, string? setupId, CancellationToken ct)
    {
        var setup = (setupId is null ? null : await settings.SetupAsync(Setups.GitHub, setupId, ct))
            ?? await ConnectSetupAsync(ct);
        var clientId = setup?.Get("github.clientId");
        var clientSecret = setup?.Get("github.clientSecret");
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new GitHubApiException(0, "No GitHub setup has a client ID and client secret.");

        var http = httpFactory.CreateClient(HttpClientName);
        using var exchange = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = JsonContent.Create(new { client_id = clientId, client_secret = clientSecret, code }),
        };
        exchange.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(exchange, ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var token = body.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token))
            throw new GitHubApiException((int)response.StatusCode,
                body.RootElement.TryGetProperty("error_description", out var d)
                    ? d.GetString() ?? "code exchange failed"
                    : "code exchange failed");

        using var who = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        who.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var whoResponse = await http.SendAsync(who, ct);
        var whoText = await whoResponse.Content.ReadAsStringAsync(ct);
        if (!whoResponse.IsSuccessStatusCode)
            throw new GitHubApiException((int)whoResponse.StatusCode, Truncate(whoText));
        using var user = JsonDocument.Parse(whoText);
        return (user.RootElement.GetProperty("id").GetInt64(),
                user.RootElement.GetProperty("login").GetString()!);
    }

    // -------------------------------------------------------- plumbing

    /// <summary>The setup a path's owner routes to (<see cref="Route"/>), or the failure that there is none.</summary>
    private async Task<SetupValues> RouteAsync(string path, CancellationToken ct) =>
        Route(await settings.SetupsAsync(Setups.GitHub, ct), OwnerOf(path))
        ?? throw new GitHubApiException(0, "No GitHub setup is active and complete (app ID, private key, organization).");

    /// <summary>
    /// An installation token for the setup that owns a repository — what the
    /// build host clones a private repository with. The same short-lived,
    /// cached token every API call sends; it expires within the hour.
    /// </summary>
    internal async Task<string> InstallationTokenForAsync(string repoFullName, CancellationToken ct) =>
        await GetInstallationTokenAsync(await RouteAsync($"/repos/{repoFullName}", ct), ct);

    /// <param name="via">The setup to go through, when the caller has already chosen — a test, a creation.</param>
    /// <param name="token">A token to send instead of the setup's installation token — the transfer token.</param>
    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct, SetupValues? via = null, string? token = null)
    {
        using var response = await SendRawAsync(method, path, body,
            token ?? await GetInstallationTokenAsync(via ?? await RouteAsync(path, ct), ct), ct);
        if (!response.IsSuccessStatusCode)
            throw new GitHubApiException((int)response.StatusCode, $"{method} {path}: {await ErrorMessageAsync(response, ct)}");
        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Length > 0 ? JsonDocument.Parse(text) : JsonDocument.Parse("{}");
    }

    /// <summary>One authenticated request, answered whatever its status — for the callers that read status and headers themselves.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method, string path, object? body, string bearer, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, "https://api.github.com" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request, ct);
    }

    /// <summary>GitHub's own "message" from an error body, or the body itself when it is not JSON.</summary>
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var error = JsonDocument.Parse(text);
            if (error.RootElement.TryGetProperty("message", out var m) && m.GetString() is { } message) return message;
        }
        catch (JsonException) { /* non-JSON error body; keep the raw text */ }
        return Truncate(text);
    }

    /// <summary>An exception's message without the "GitHub API (403) — " the type puts in front, for rewording.</summary>
    private static string Unprefixed(GitHubApiException e)
    {
        var dash = e.Message.IndexOf("— ", StringComparison.Ordinal);
        return dash < 0 ? e.Message : e.Message[(dash + 2)..];
    }

    /// <summary>JWT → installation lookup for the setup's org → installation token, cached per App and org.</summary>
    private async Task<string> GetInstallationTokenAsync(SetupValues setup, CancellationToken ct)
    {
        var appId = setup.Get("github.appId")?.Trim() ?? "";
        var pem = setup.Get("github.privateKey") ?? "";
        var org = setup.Get("github.organization")?.Trim() ?? "";
        if (appId.Length == 0 || pem.Length == 0 || org.Length == 0)
            throw new GitHubApiException(0, $"The GitHub setup “{setup.Name}” is not complete (app ID, private key, organization).");

        // Key the cache on the credentials so a settings change never serves a stale token.
        var cacheKey = $"{appId}:{org}";
        lock (_tokens)
        {
            if (_tokens.TryGetValue(cacheKey, out var hit) && hit.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
                return hit.Token;
        }

        await _tokenGate.WaitAsync(ct);
        try
        {
            lock (_tokens)
            {
                if (_tokens.TryGetValue(cacheKey, out var again) && again.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
                    return again.Token;
            }

            var jwt = GitHubAppJwt.Create(appId, pem);
            var http = httpFactory.CreateClient(HttpClientName);

            long installationId;
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/app/installations"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                using var response = await http.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new GitHubApiException((int)response.StatusCode, $"listing installations: {Truncate(text)}");
                using var installations = JsonDocument.Parse(text);
                installationId = installations.RootElement.EnumerateArray()
                    .Where(i => string.Equals(
                        i.GetProperty("account").GetProperty("login").GetString(), org,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(i => i.GetProperty("id").GetInt64())
                    .FirstOrDefault();
                if (installationId == 0)
                    throw new GitHubApiException(404,
                        $"The app is not installed on '{org}'. Install it on the organization, then retry.");
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.github.com/app/installations/{installationId}/access_tokens"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                using var response = await http.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new GitHubApiException((int)response.StatusCode, $"creating installation token: {Truncate(text)}");
                using var body = JsonDocument.Parse(text);
                var token = body.RootElement.GetProperty("token").GetString()!;
                var expires = body.RootElement.GetProperty("expires_at").GetDateTimeOffset();
                lock (_tokens) _tokens[cacheKey] = (token, expires);
                log.LogInformation("GitHub installation token for {Org} refreshed; expires {ExpiresAt}.", org, expires);
                return token;
            }
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static GitHubRepo ReadRepo(JsonElement repo) => new(
        repo.GetProperty("id").GetInt64(),
        repo.GetProperty("full_name").GetString()!,
        repo.GetProperty("owner").GetProperty("login").GetString()!,
        repo.TryGetProperty("default_branch", out var branch) ? branch.GetString() ?? "main" : "main",
        repo.TryGetProperty("archived", out var archived) && archived.GetBoolean());

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}

public sealed record GitHubRepo(long Id, string FullName, string OwnerLogin, string DefaultBranch, bool Archived);

public sealed class GitHubApiException(int statusCode, string message)
    : Exception($"GitHub API {(statusCode == 0 ? "" : $"({statusCode}) ")}— {message}")
{
    public int StatusCode { get; } = statusCode;
}
