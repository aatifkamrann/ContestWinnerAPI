using System.Text;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using WinnersPortal.Domain;
using WinnersPortal.Services.Activity;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WinnersPortal.Api.Activity;

/// <summary>
/// Around the two webhook endpoints: a delivery from GitHub or the identity
/// provider becomes a third-party row in the activity log — what arrived
/// (headers and body, the signature masked) and what the portal answered —
/// the incoming twin of <see cref="ExternalCallRecorder"/>. A delivery the
/// signature check refused is recorded too; that is the one worth seeing.
///
/// The request is buffered so the controller still reads every byte the
/// signature is over, and the answer is captured on its way out and copied
/// through unchanged. Recording that fails is swallowed: GitHub and the
/// provider get the same answer either way.
/// </summary>
public sealed class WebhookLogMiddleware(RequestDelegate next)
{
    private static readonly string[] NoSecrets = [];

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method) || ExternalWebhooks.ServiceOf(ctx.Request.Path.Value) is not { } service)
        {
            await next(ctx);
            return;
        }

        var at = DateTimeOffset.UtcNow;
        ctx.Request.EnableBuffering();
        var original = ctx.Response.Body;
        using var answer = new MemoryStream();
        ctx.Response.Body = answer;
        var clock = Stopwatch.StartNew();
        Exception? failure = null;
        try
        {
            await next(ctx);
        }
        catch (Exception e)
        {
            failure = e;
            throw;
        }
        finally
        {
            clock.Stop();
            ctx.Response.Body = original;
            answer.Position = 0;
            if (failure is null) await answer.CopyToAsync(original);
            await RecordAsync(ctx, service, at, clock.ElapsedMilliseconds, answer, failure);
        }
    }

    private static async Task RecordAsync(
        HttpContext ctx, string service, DateTimeOffset at, long elapsed, MemoryStream answer, Exception? failure)
    {
        try
        {
            var body = await ReadRequestAsync(ctx.Request);
            var type = ctx.Request.ContentType is { } t ? t.Split(';')[0].Trim().ToLowerInvariant() : null;
            var shownBody = body is null ? null : ExternalExchange.Body(service, type, body, NoSecrets, fromProvider: true);
            var url = new Uri(ctx.Request.GetDisplayUrl());
            var sent = ExternalExchange.RequestText(service, ctx.Request.Method, url,
                ctx.Request.Headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.Select(v => v ?? ""))),
                shownBody, NoSecrets);

            var status = failure is null ? ctx.Response.StatusCode : StatusCodes.Status500InternalServerError;
            var answered = Encoding.UTF8.GetString(answer.ToArray());
            var answerType = ctx.Response.ContentType is { } a ? a.Split(';')[0].Trim().ToLowerInvariant() : null;
            var responded = ExternalExchange.ResponseText(status, ReasonPhrases.GetReasonPhrase(status), elapsed,
                ctx.Response.Headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.Select(v => v ?? ""))),
                failure is not null
                    ? $"The portal failed while handling it: {failure.GetType().Name}: {failure.Message}"
                    : answered.Length == 0 ? null : ExternalExchange.Body(service, answerType, answered, NoSecrets, fromProvider: false),
                NoSecrets);

            ctx.RequestServices.GetRequiredService<ActivityLog>().Record(new ActivityEvent
            {
                UserId = ExternalWebhooks.UserOf(service, body),
                Kind = ActivityKinds.External,
                Service = service,
                Method = ctx.Request.Method.ToUpperInvariant(),
                Path = ActivityNames.Trim(ctx.Request.Path.Value, ActivityNames.MaxPath) ?? "/",
                Action = ExternalWebhooks.Action(service, ctx.Request.Path.Value),
                Subject = ActivityNames.Trim(
                    ExternalWebhooks.Subject(service, ctx.Request.Headers["X-GitHub-Event"].ToString(), body),
                    ActivityNames.MaxSubject),
                Status = status,
                // The sender's address and client, which is how a delivery
                // that did not come from GitHub or the provider shows itself.
                Ip = ctx.Connection.RemoteIpAddress?.ToString(),
                UserAgent = ActivityNames.Trim(ctx.Request.Headers.UserAgent, ActivityNames.MaxAgent),
                DurationMs = (int)Math.Min(elapsed, int.MaxValue),
                Request = sent,
                Response = responded,
                AtUtc = at,
            });
        }
        catch
        {
            // The log is never the reason a delivery is answered differently.
        }
    }

    /// <summary>The body as text, up to a megabyte; the controller has already read it, so it is rewound first.</summary>
    private static async Task<string?> ReadRequestAsync(HttpRequest request)
    {
        if (!request.Body.CanSeek) return null;
        request.Body.Position = 0;
        if (request.Body.Length == 0) return null;
        if (request.Body.Length > ExternalExchange.MaxBuffered)
            return ExternalExchange.Described(request.Body.Length, request.ContentType, "too long to record");
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
