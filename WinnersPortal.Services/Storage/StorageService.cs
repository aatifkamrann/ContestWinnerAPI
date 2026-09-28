using WinnersPortal.Services.Auth;
using System.Text.RegularExpressions;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Storage;

/// <summary>
/// Everything that talks to the object store. Two audiences, one signer:
/// browser-facing URLs are signed for <c>storage.publicUrl</c> (the address
/// a browser can actually reach — inside compose the API knows the store as
/// <c>storage:9000</c>, which resolves nowhere else), while the server's own
/// reads and writes go to <c>storage.endpoint</c>. The host is part of the
/// signature, so the two are never interchangeable.
///
/// <para>A browser only ever reads from the store, on a signed download or
/// preview link. Every upload arrives through the API and is written from
/// here (<see cref="StoreAsync"/>), as the identity proof's images are — so
/// no bucket is ever asked to answer a page, and it needs no CORS rule.</para>
///
/// <para>A portal can hold several stores (Settings/Setups.cs), one of
/// them active. A new file goes to the active store — <see cref="UploadSetupAsync"/>
/// — and its row records which, so every later read, preview and delete
/// goes back to the store that has the bytes, whichever store is active by
/// then. A null on an older row is the main setup, where every file lived
/// before there was a choice.</para>
/// </summary>
public sealed partial class StorageService(
    SettingsService settings,
    IHttpClientFactory httpFactory,
    ILogger<StorageService> log)
{
    public const string HttpClientName = "storage";

    public static readonly TimeSpan DownloadLinkLife = TimeSpan.FromMinutes(5);

    /// <summary>How long a reserved slot waits for its bytes before a later reserve drops it.</summary>
    public static readonly TimeSpan ReservationLife = TimeSpan.FromMinutes(15);

    /// <summary>Whether a new file has somewhere to go.</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default) =>
        await UploadSetupAsync(ct) is not null;

    /// <summary>The setup a new file goes to: the active store, once it is complete. Null otherwise.</summary>
    public async Task<string?> UploadSetupAsync(CancellationToken ct = default) =>
        await settings.ActiveSetupAsync(Setups.Storage, ct) is { } active && Config(active) is not null
            ? active.Id
            : null;

    /// <summary>The setup a file row names — the main one for a row from before there were several.</summary>
    public static string SetupOf(string? recorded) => recorded ?? Setups.MainId;

    public async Task<long> MaxUploadBytesAsync(CancellationToken ct = default)
    {
        var mb = int.TryParse(await settings.GetAsync("limits.maxUploadMb", ct), out var m) && m > 0 ? m : 25;
        return mb * 1024L * 1024L;
    }

    // ------------------------------------------------- browser-facing URLs

    // AbsoluteUri, never ToString(): ToString() unescapes the path for
    // readability, so a key with a space comes back with a literal space —
    // which no longer matches the %20 the signature was computed over.

    /// <summary>
    /// A five-minute GET that forces a download — see StorageRules.ContentDisposition —
    /// or, for the one preview that needs it, opens inline (StorageRules.OpensInline).
    /// </summary>
    public async Task<string> DownloadUrlAsync(
        string? setup, string key, string fileName, string? contentType, CancellationToken ct, bool inline = false)
    {
        var c = await RequiredConfigAsync(setup, ct);
        var overrides = new List<KeyValuePair<string, string>>
        {
            new("response-content-disposition", StorageRules.ContentDisposition(fileName, inline)),
        };
        if (!string.IsNullOrWhiteSpace(contentType))
            overrides.Add(new("response-content-type", contentType));
        return S3Presign.Url(c.PublicUrl, c.Bucket, key, c.AccessKey, c.SecretKey, c.Region,
            "GET", DownloadLinkLife, DateTimeOffset.UtcNow, overrides).AbsoluteUri;
    }

    // ------------------------------------------------- server-side objects

    /// <summary>
    /// One upload's bytes, written as the request that carries them is read.
    /// With a length known they stream straight through — S3 wants the
    /// length before the first byte, and a browser always says it; without
    /// one they are spooled to a temporary file first. Null when they are
    /// more than <paramref name="maxBytes"/>, and then nothing was written.
    /// </summary>
    public async Task<long?> StoreAsync(
        string? setup, string key, Stream body, long? length, string contentType, long maxBytes, CancellationToken ct)
    {
        if (length is { } known)
        {
            if (known > maxBytes) return null;
            await PutAsync(setup, key, body, known, contentType, ct);
            return known;
        }
        await using var spool = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes) return null;
            await spool.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        spool.Position = 0;
        await PutAsync(setup, key, spool, total, contentType, ct);
        return total;
    }

    /// <summary>A whole object, for the text a page shows in place — small by StorageRules.MaxTextPreviewBytes.</summary>
    public async Task<byte[]> ReadAsync(string? setup, string key, CancellationToken ct)
    {
        using var response = await SendAsync(await RequiredConfigAsync(setup, ct), HttpMethod.Get, key, null, ct);
        await ThrowIfFailedAsync(response, "GET", key, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Null when the object does not exist; otherwise its true size — the answer to "did the upload arrive".</summary>
    public async Task<long?> HeadAsync(string? setup, string key, CancellationToken ct)
    {
        using var response = await SendAsync(await RequiredConfigAsync(setup, ct), HttpMethod.Head, key, null, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ThrowIfFailedAsync(response, "HEAD", key, ct);
        return response.Content.Headers.ContentLength ?? 0;
    }

    public Task PutAsync(string? setup, string key, Stream content, long length, string contentType, CancellationToken ct) =>
        PutAsync(RequiredConfigAsync(setup, ct), key, content, length, contentType, ct);

    private async Task PutAsync(Task<StorageConfig> config, string key, Stream content, long length, string contentType, CancellationToken ct)
    {
        using var body = new StreamContent(content);
        body.Headers.ContentLength = length;
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        using var response = await SendAsync(await config, HttpMethod.Put, key, body, ct);
        await ThrowIfFailedAsync(response, "PUT", key, ct);
    }

    /// <summary>Idempotent — deleting what is already gone is success, on S3 and here.</summary>
    public async Task DeleteAsync(string? setup, string key, CancellationToken ct) =>
        await DeleteAsync(await RequiredConfigAsync(setup, ct), key, ct);

    private async Task DeleteAsync(StorageConfig config, string key, CancellationToken ct)
    {
        using var response = await SendAsync(config, HttpMethod.Delete, key, null, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            await ThrowIfFailedAsync(response, "DELETE", key, ct);
    }

    private async Task<string> GetStringAsync(StorageConfig config, string key, CancellationToken ct)
    {
        using var response = await SendAsync(config, HttpMethod.Get, key, null, ct);
        await ThrowIfFailedAsync(response, "GET", key, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ------------------------------------------------------ the round-trip

    /// <summary>
    /// The blueprint's storage test: upload, read back through a signed URL,
    /// delete — the exact path every attachment takes. A missing bucket is
    /// created first when the credentials may; on R2 and locked-down keys
    /// that is a clear error instead. One setup at a time, active or not —
    /// proving a store before it takes files is the point.
    ///
    /// <para>Then the one thing about the browser's side the server can
    /// judge: a page on https opens nothing from a public URL on http, so
    /// that pairing is refused here rather than at the first preview. The
    /// page that pressed the button and the Web URL are the addresses files
    /// are opened from.</para>
    /// </summary>
    public async Task<(bool Ok, string Detail)> TestAsync(string setupId, string? pageOrigin, CancellationToken ct)
    {
        var setup = await settings.SetupAsync(Setups.Storage, setupId, ct);
        if (setup is null)
            return (false, "That storage setup is not in the list any more — reload the settings.");
        if (Config(setup) is not { } config)
            return (false, "This setup is not complete — endpoint, bucket, and both keys are required.");
        var key = $"health/roundtrip-{Guid.NewGuid():N}.txt";
        var payload = $"winners-portal storage test {DateTimeOffset.UtcNow:O}";
        try
        {
            try
            {
                await PutAsync(Task.FromResult(config), key, StringStream(payload), payload.Length, "text/plain", ct);
            }
            catch (StorageException e) when (e.Code == "NoSuchBucket")
            {
                await CreateBucketAsync(config, ct);
                await PutAsync(Task.FromResult(config), key, StringStream(payload), payload.Length, "text/plain", ct);
            }
            var readBack = await GetStringAsync(config, key, ct);
            await DeleteAsync(config, key, ct);
            if (readBack != payload)
                return (false, "The file read back different from what was written — is something rewriting objects?");

            var origins = BrowserOrigins(pageOrigin, await settings.GetAsync(WebOrigin.WebUrlKey, ct));
            if (InsecureAddressProblem(config.PublicUrl, origins) is { } insecure)
                return (false, insecure);
            return (true, "Uploaded, read back through a signed URL, and deleted a test file. Uploads reach this "
                + "store through the portal, so the bucket needs no CORS rule; downloads and previews open "
                + $"from {config.PublicUrl.GetLeftPart(UriPartial.Authority)}, which a browser must be able to reach.");
        }
        catch (Exception e) when (e is StorageException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(e, "Storage round-trip failed.");
            return (false, TestFailureText(config, e));
        }
    }

    /// <summary>
    /// Pure: what a failed test says — the store's own words, except where
    /// they send the admin to the wrong fix. "The Access Key Id you provided
    /// does not exist in our records" reads like a typo in the field, and is
    /// as often a key made on another store, or never made at all: typing a
    /// key here does not create it there. "Access Denied" names neither the
    /// key nor the bucket, and is most often a bucket renamed here but not in
    /// the key's policy, which names buckets one by one. The MinIO fixes are
    /// mc commands: recent MinIO consoles no longer manage users or policies.
    /// </summary>
    public static string TestFailureText(StorageConfig c, Exception e)
    {
        var store = c.Endpoint.GetLeftPart(UriPartial.Authority);
        return e switch
        {
            StorageException { Code: "InvalidAccessKeyId" } =>
                $"The store at {store} has no access key “{c.AccessKey}”. Create it on that store "
                + $"(on MinIO, a user named after the key: mc admin user add <alias> {c.AccessKey} <secret key>, "
                + $"then mc admin policy attach <alias> <policy> --user {c.AccessKey}), "
                + "or set the S3 endpoint to the store that holds it.",
            StorageException { Code: "AccessDenied", Operation: "CREATE BUCKET" } =>
                $"The store at {store} has no bucket “{c.Bucket}”, and key “{c.AccessKey}” may not create it. "
                + $"Create the bucket yourself, or allow s3:CreateBucket on arn:aws:s3:::{c.Bucket} in the key’s policy.",
            StorageException { Code: "AccessDenied", Operation: var op } =>
                $"The store at {store} will not let key “{c.AccessKey}” {DeniedVerb(op)} bucket “{c.Bucket}”. "
                + "The key’s policy names the buckets it may use, one by one: it needs "
                + $"s3:PutObject, s3:GetObject and s3:DeleteObject on arn:aws:s3:::{c.Bucket}/*, "
                + $"and s3:CreateBucket on arn:aws:s3:::{c.Bucket} if the portal is to create the bucket. "
                + "On MinIO, load the corrected policy with mc admin policy create <alias> <policy> policy.json: "
                + "it replaces the one of that name, and the user keeps it. "
                + "On AWS, a bucket of that name in another account is refused the same way.",
            _ => e.Message,
        };
    }

    private static string DeniedVerb(string? op) => op switch
    {
        "PUT" => "write to",
        "GET" or "HEAD" => "read from",
        "DELETE" => "delete from",
        _ => "use",
    };

    private async Task CreateBucketAsync(StorageConfig c, CancellationToken ct)
    {
        // CanonicalPath(bucket, "") yields "/bucket/"; S3 reads that as the bucket itself.
        var url = S3Presign.Url(c.Endpoint, c.Bucket, "", c.AccessKey, c.SecretKey, c.Region,
            "PUT", TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow);
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Conflict)
            await ThrowIfFailedAsync(response, "CREATE BUCKET", c.Bucket, ct);
        log.LogInformation("Created bucket '{Bucket}'.", c.Bucket);
    }

    // ------------------------------------------------------ the browser's half

    /// <summary>
    /// Pure: the one thing about the browser's address the server can judge
    /// by itself. A page on https loads nothing from an http address — the
    /// browser blocks it as mixed content before a byte is asked for, and
    /// nothing reaches the store's log — so a Public base URL on http fails
    /// every preview and download however well the server's round-trip
    /// went. Loopback names are the exception browsers make, which is what
    /// a local stack runs on.
    /// </summary>
    public static string? InsecureAddressProblem(Uri publicUrl, IReadOnlyList<string> origins)
    {
        if (publicUrl.Scheme != Uri.UriSchemeHttp || IsLoopback(publicUrl)) return null;
        var secure = origins.FirstOrDefault(o => o.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (secure is null) return null;
        var https = new UriBuilder(publicUrl) { Scheme = Uri.UriSchemeHttps, Port = publicUrl.IsDefaultPort ? -1 : publicUrl.Port };
        return $"The server’s round-trip passed, but a page on {secure} cannot open files from "
            + $"{publicUrl.GetLeftPart(UriPartial.Authority)}: a browser loads nothing from an http address into an https page. "
            + $"Give the store a certificate for {publicUrl.Host} and set the Public base URL to "
            + $"{https.Uri.GetLeftPart(UriPartial.Authority)} (on MinIO, public.crt and private.key in its certs directory; "
            + "it serves https once it finds them).";
    }

    private static bool IsLoopback(Uri u) =>
        u.IsLoopback || u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pure: the origins files are opened from — the page that asked for
    /// the test, then the Web URL members arrive at — scheme, host and
    /// port, each once. An admin reaching the portal by another address
    /// than members do is why there can be two.
    /// </summary>
    public static IReadOnlyList<string> BrowserOrigins(string? pageOrigin, string? webUrl)
    {
        var origins = new List<string>();
        foreach (var value in new[] { pageOrigin, webUrl })
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                continue;
            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase)) origins.Add(origin);
        }
        return origins;
    }

    // ------------------------------------------------------------ plumbing

    private async Task<HttpResponseMessage> SendAsync(
        StorageConfig c, HttpMethod method, string key, HttpContent? content, CancellationToken ct)
    {
        var url = S3Presign.Url(c.Endpoint, c.Bucket, key, c.AccessKey, c.SecretKey, c.Region,
            method.Method, TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow);
        using var request = new HttpRequestMessage(method, url) { Content = content };
        return await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string op, string key, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        // S3 errors are XML; the Code element is the part worth keeping.
        var code = ErrorCodePattern().Match(body).Groups[1].Value;
        var message = ErrorMessagePattern().Match(body).Groups[1].Value;
        throw new StorageException((int)response.StatusCode,
            code.Length > 0 ? code : null,
            $"{op} {key}: {(message.Length > 0 ? message : $"the store answered {(int)response.StatusCode}")}",
            op);
    }

    /// <summary>
    /// The store a file lives in: the setup its row names, whether or not it
    /// is still the active one — making another store active stops new files
    /// going there, it does not strand the ones already in it.
    /// </summary>
    private async Task<StorageConfig> RequiredConfigAsync(string? setup, CancellationToken ct)
    {
        var id = SetupOf(setup);
        var values = await settings.SetupAsync(Setups.Storage, id, ct)
            ?? throw new StorageException(0, null, $"The storage setup this file was saved to ('{id}') is no longer in the settings.");
        return Config(values)
            ?? throw new StorageException(0, null, $"The storage setup “{values.Name}” is not complete (endpoint, bucket, access key, secret key).");
    }

    /// <summary>
    /// Pure: a setup's values as a store to sign for, or null while one of
    /// the four it needs is missing. Trimmed here as well as on save, for a
    /// value saved before the save trimmed, or set by an environment variable.
    /// </summary>
    public static StorageConfig? Config(SetupValues s)
    {
        var endpoint = s.Get("storage.endpoint")?.Trim();
        var bucket = s.Get("storage.bucket")?.Trim();
        var accessKey = s.Get("storage.accessKey")?.Trim();
        var secretKey = s.Get("storage.secretKey")?.Trim();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(bucket)
            || string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)) return null;

        var publicUri = Uri.TryCreate(s.Get("storage.publicUrl")?.Trim(), UriKind.Absolute, out var p) ? p : endpointUri;
        var region = s.Get("storage.region")?.Trim();
        return new StorageConfig(endpointUri, publicUri, bucket, accessKey, secretKey,
            string.IsNullOrWhiteSpace(region) ? "us-east-1" : region);
    }

    private static MemoryStream StringStream(string s) =>
        new(System.Text.Encoding.UTF8.GetBytes(s));

    [GeneratedRegex("<Code>([^<]+)</Code>")]
    private static partial Regex ErrorCodePattern();

    [GeneratedRegex("<Message>([^<]+)</Message>")]
    private static partial Regex ErrorMessagePattern();
}

public sealed record StorageConfig(
    Uri Endpoint, Uri PublicUrl, string Bucket, string AccessKey, string SecretKey, string Region);

public sealed class StorageException(int statusCode, string? code, string message, string? operation = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>The S3 error code ("NoSuchBucket", "AccessDenied") when the store sent one.</summary>
    public string? Code { get; } = code;

    /// <summary>What was refused ("PUT", "GET", "DELETE", "HEAD", "CREATE BUCKET") when the store refused it.</summary>
    public string? Operation { get; } = operation;
}
