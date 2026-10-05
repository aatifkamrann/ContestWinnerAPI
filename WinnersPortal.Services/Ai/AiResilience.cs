using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// What stands between <see cref="AiProviderClient"/> and the wire: a
/// timeout per attempt, a retry or two on the failures a provider's bad
/// minute produces, and a pause once it has been failing — the three
/// knobs on Settings → AI automation (<see cref="AiLimits"/>), read live
/// on every call rather than bound at startup. Outermost to innermost: a
/// total timeout, the retries, the circuit breaker, the attempt timeout;
/// the activity recorder sits inside all of them, so every attempt that
/// left the building is its own row in the log, as it was on the wire.
/// </summary>
public static class AiResilience
{
    public const string PipelineName = "ai";

    /// <summary>The pause trips once half of at least four calls in a minute have failed.</summary>
    public const double FailureRatio = 0.5;
    public const int MinimumThroughput = 4;
    public static readonly TimeSpan SamplingDuration = TimeSpan.FromSeconds(60);

    /// <summary>The first wait before a retry; the next doubles it, each with jitter. A Retry-After header is honoured instead.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Room for the waits between attempts on top of the attempts
    /// themselves: a provider's Retry-After of a minute is waited for, two
    /// of them are not, and the call is given up as timed out.
    /// </summary>
    public static readonly TimeSpan BackoffAllowance = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Registers the pipeline on the named client; chain it before
    /// <c>RecordExternalCalls</c>, so the recorder sits inside it. One
    /// pipeline per host: the breaker that pauses one provider's calls
    /// leaves another provider's open, which is what lets a standby setup
    /// answer while the active provider rests (<see cref="AiCaller"/>). The
    /// limits are read through <see cref="AiOptions"/> unless a reader is
    /// given — a test's own numbers.
    /// </summary>
    public static IHttpClientBuilder AddAiResilience(
        this IHttpClientBuilder builder, Func<IServiceProvider, Func<CancellationToken, Task<AiLimitValues>>>? limits = null)
    {
        builder.AddResilienceHandler(PipelineName, (pipeline, context) =>
            {
                var read = limits?.Invoke(context.ServiceProvider)
                    ?? (ct => context.ServiceProvider.GetRequiredService<AiOptions>().LimitsAsync(ct));
                Configure(pipeline, read);
            })
            .SelectPipelineByAuthority();
        return builder;
    }

    /// <summary>The pipeline over a reader of the limits in force — the settings, or a test's own numbers.</summary>
    public static void Configure(
        ResiliencePipelineBuilder<HttpResponseMessage> pipeline, Func<CancellationToken, Task<AiLimitValues>> limits)
    {
        pipeline
            .AddTimeout(new HttpTimeoutStrategyOptions
            {
                Name = "ai-total",
                TimeoutGenerator = async args => (await limits(args.Context.CancellationToken)).TotalTimeout,
            })
            .AddRetry(new HttpRetryStrategyOptions
            {
                Name = "ai-retry",
                // The ceiling the setting moves under: the predicate stops at
                // the saved number of retries, read on every attempt.
                MaxRetryAttempts = AiLimits.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                Delay = FirstDelay,
                UseJitter = true,
                ShouldRetryAfterHeader = true,
                ShouldHandle = async args =>
                    Transient(args.Outcome)
                    && args.AttemptNumber < (await limits(args.Context.CancellationToken)).Retries,
            })
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                Name = "ai-breaker",
                FailureRatio = FailureRatio,
                MinimumThroughput = MinimumThroughput,
                SamplingDuration = SamplingDuration,
                // A pause of zero means the breaker counts nothing, so it never trips.
                ShouldHandle = async args =>
                    Transient(args.Outcome)
                    && (await limits(args.Context.CancellationToken)).PauseSeconds > 0,
                BreakDurationGenerator = async args =>
                    TimeSpan.FromSeconds(Math.Max(1, (await limits(args.Context.CancellationToken)).PauseSeconds)),
            })
            .AddTimeout(new HttpTimeoutStrategyOptions
            {
                Name = "ai-attempt",
                TimeoutGenerator = async args =>
                    TimeSpan.FromSeconds((await limits(args.Context.CancellationToken)).CallTimeoutSeconds),
            });
    }

    /// <summary>
    /// A failure the next attempt may not meet: a 429, a 5xx, a 408, an
    /// attempt that timed out, or a connection that never answered. A 4xx
    /// is the request's own fault and is answered, not retried; a 200 that
    /// carries no answer is read after the pipeline, by
    /// <see cref="AiProviderRequests.ExtractText"/>.
    /// </summary>
    public static bool Transient(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is HttpRequestException or TimeoutRejectedException
        || outcome.Result is { } response
            && (response.StatusCode == HttpStatusCode.TooManyRequests
                || response.StatusCode == HttpStatusCode.RequestTimeout
                || (int)response.StatusCode >= 500);
}
