using Stopwatch = System.Diagnostics.Stopwatch;
using System.Net.Http.Json;
using WinnersPortal.Domain;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Auth;

namespace WinnersPortal.Api.Activity;

/// <summary>
/// The last handler before the wire on every named HTTP client the portal
/// calls an outside service with: each call becomes a third-party row in
/// the activity log — what was sent, what came back, how long it took, and
/// whose click set it off. What the row keeps is decided by
/// <see cref="ExternalExchange"/>, which masks secrets and a verification's
/// extracted identity before anything is written.
///
/// The call itself is never changed by being recorded: a body is read only
/// when it is already in memory (a string, a form, JSON), a response only
/// when it is readable text under a megabyte, and a failure to record is
/// swallowed. A row that cannot be written is lost; the call is not.
/// </summary>
public sealed class ExternalCallRecorder(string service, ActivityLog log, IHttpContextAccessor http) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;
        var secrets = request.Options.TryGetValue(ExternalExchange.SecretsKey, out var marked) ? marked : [];
        var sent = await SafelyAsync(() => RequestTextAsync(request, secrets, ct));
        var clock = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch (Exception e)
        {
            var cancelled = e is OperationCanceledException;
            Record(request, at, clock.ElapsedMilliseconds, 0, sent,
                ExternalExchange.NoAnswerText(clock.ElapsedMilliseconds, e, cancelled, secrets));
            throw;
        }
        var elapsed = clock.ElapsedMilliseconds;
        var answered = await SafelyAsync(() => ResponseTextAsync(response, elapsed, secrets, ct));
        Record(request, at, elapsed, (int)response.StatusCode, sent, answered);
        return response;
    }

    private async Task<string> RequestTextAsync(HttpRequestMessage request, IReadOnlyCollection<string> secrets, CancellationToken ct)
    {
        string? body = null;
        if (request.Content is { } content)
        {
            var type = ExternalExchange.MediaType(content.Headers);
            var length = content.Headers.ContentLength;
            // Storage carries files; they are described, never kept. A
            // stream is read once — reading it here would send nothing.
            body = service == ExternalServices.Storage
                ? ExternalExchange.Described(length, type, "file contents are not recorded")
                : !(content is ByteArrayContent or JsonContent)
                    ? ExternalExchange.Described(length, type, "a stream, not recorded")
                    : !ExternalExchange.Readable(type)
                        ? ExternalExchange.Described(length, type, "not text, not recorded")
                        : ExternalExchange.Body(service, type, await content.ReadAsStringAsync(ct), secrets, fromProvider: false);
        }
        return ExternalExchange.RequestText(service, request.Method.Method, request.RequestUri!,
            request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()),
            body, secrets);
    }

    private async Task<string> ResponseTextAsync(HttpResponseMessage response, long elapsed, IReadOnlyCollection<string> secrets, CancellationToken ct)
    {
        var content = response.Content;
        var type = ExternalExchange.MediaType(content.Headers);
        var length = content.Headers.ContentLength;
        string? body;
        if (length == 0) body = null;
        else if (service == ExternalServices.Storage && response.IsSuccessStatusCode)
            body = ExternalExchange.Described(length, type, "file contents are not recorded");
        else if (!ExternalExchange.Readable(type))
            body = ExternalExchange.Described(length, type, "not text, not recorded");
        else if (length > ExternalExchange.MaxBuffered)
            body = ExternalExchange.Described(length, type, "too long to record");
        else
        {
            // Buffered, so the caller still reads the whole body after us.
            await content.LoadIntoBufferAsync();
            var text = await content.ReadAsStringAsync(ct);
            body = text.Length == 0 ? null : ExternalExchange.Body(service, type, text, secrets, fromProvider: true);
        }
        return ExternalExchange.ResponseText((int)response.StatusCode, response.ReasonPhrase, elapsed,
            response.Headers.Concat(content.Headers), body, secrets);
    }

    private void Record(HttpRequestMessage request, DateTimeOffset at, long elapsed, int status, string? sent, string? answered)
    {
        try
        {
            // Whose request set the call off, when there is one: the service's
            // note first (a sign-in knows the person before the session does),
            // then the principal. A worker's call has no request and no person.
            var ctx = http.HttpContext;
            var note = ctx?.RequestServices.GetService<ActivityNote>();
            var url = request.RequestUri!;
            log.Record(new ActivityEvent
            {
                UserId = note?.UserId ?? (ctx is null ? null : Principal.UserId(ctx.User)),
                Visitor = ctx is null ? null : Visitors.VisitorOf(ctx.Request),
                Kind = ActivityKinds.External,
                Service = service,
                Method = request.Method.Method,
                Path = ActivityNames.Trim(ExternalExchange.Path(service, url), ActivityNames.MaxPath) ?? url.Host,
                Action = ActivityNames.Trim(ExternalExchange.Action(url), ActivityNames.MaxSubject) ?? "Called a service",
                Subject = request.Options.TryGetValue(ExternalExchange.SubjectKey, out var subject)
                    ? ActivityNames.Trim(subject, ActivityNames.MaxSubject)
                    : null,
                Page = ctx is null ? null : ActivityNames.PageOf(ctx.Request.Headers.Referer),
                Status = status,
                DurationMs = (int)Math.Min(elapsed, int.MaxValue),
                Request = sent,
                Response = answered,
                AtUtc = at,
            });
        }
        catch
        {
            // The log is never the reason a call fails.
        }
    }

    private static async Task<string?> SafelyAsync(Func<Task<string>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception e)
        {
            return $"[could not be recorded: {e.GetType().Name}]";
        }
    }
}

/// <summary>How a named client gets its recorder: <c>AddHttpClient(...).RecordExternalCalls(ExternalServices.Ai)</c>.</summary>
public static class ExternalCallRecording
{
    public static IHttpClientBuilder RecordExternalCalls(this IHttpClientBuilder builder, string service) =>
        builder.AddHttpMessageHandler(sp => new ExternalCallRecorder(
            service, sp.GetRequiredService<ActivityLog>(), sp.GetRequiredService<IHttpContextAccessor>()));
}
