namespace WinnersPortal.Domain;

public enum AiArtifactStatus
{
    /// <summary>Queued for the worker. Nothing AI runs in a request path.</summary>
    Pending = 0,

    /// <summary>Output is present and valid — a draft for a person to read.</summary>
    Done = 1,

    /// <summary>Gave up after retries; <see cref="AiArtifact.Note"/> says why.</summary>
    Failed = 2,

    /// <summary>Deliberately not run — feature switched off, or the daily
    /// ceiling was reached and skipped-not-queued is the rule.</summary>
    Skipped = 3,
}

/// <summary>
/// One AI-drafted output: the job and its cache in a single row. A feature
/// has at most one artifact per subject; re-requesting re-queues the same
/// row, and the worker's content hash makes an unchanged re-run free.
/// Everything here is a draft — nothing an artifact says ever decides who
/// wins, what gets rejected, or what gets paid.
/// </summary>
public sealed class AiArtifact
{
    public Guid Id { get; set; }

    public AiFeature Feature { get; set; }

    /// <summary>The opportunity (or, for digests, the entry) this draft is about.</summary>
    public Guid SubjectId { get; set; }

    public AiArtifactStatus Status { get; set; } = AiArtifactStatus.Pending;

    /// <summary>Hash of the exact input the current output answers — the cache key.</summary>
    public string? InputHash { get; set; }

    /// <summary>The validated draft, canonical JSON. Shape depends on the feature.</summary>
    public string? OutputJson { get; set; }

    /// <summary>Why a run failed or was skipped — shown to the requester, plainly.</summary>
    public string? Note { get; set; }

    /// <summary>"gemini", "anthropic", or "local" for features that never leave the server.</summary>
    public string? Provider { get; set; }

    public string? Model { get; set; }

    /// <summary>
    /// The version of the prompt the current output answers (the feature's
    /// number in AiPrompts.Version when it was drafted). Null for the local
    /// spam scan, and for drafts made before prompts carried a version.
    /// </summary>
    public int? PromptVersion { get; set; }

    /// <summary>
    /// What the current output cost in tokens, as the provider counted
    /// them: sent and answered. Null for the local spam scan, for a draft
    /// made before calls carried their count, and when the provider sent
    /// none. Untouched by a free hash-match re-run, like the output.
    /// </summary>
    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    /// <summary>Provider attempts on the current request; three is the give-up.</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Not before this moment, after a failure worth trying again: a minute
    /// after the first, five after the second, so a provider's bad minute
    /// is waited out rather than spent on. Null means the next sweep.
    /// </summary>
    public DateTimeOffset? NextAttemptAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When the current output was produced. Untouched by a free hash-match re-run.</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
