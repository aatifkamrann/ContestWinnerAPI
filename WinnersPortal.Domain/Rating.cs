namespace WinnersPortal.Domain;

/// <summary>
/// One party's score of the other after a finished deal. Ratings hang off the
/// award and open only once it is marked paid — the deal has to finish before
/// either side scores it, which is also what makes them hard to farm: every
/// rating cost somebody a real award.
///
/// Two rows at most per award (winner→client and client→winner), enforced by
/// a unique index on (AwardId, ByUserId). Editable by the author — a score is
/// an opinion, and opinions get to change — with the edit stamped.
/// </summary>
public sealed class Rating
{
    // Column limits: the one place a length is stated; the rules and the
    // DbContext both read it here.
    public const int MaxComment = 600;

    public Guid Id { get; set; }

    public Guid AwardId { get; set; }
    public Award? Award { get; set; }

    /// <summary>Who scored — the award's client or its winner, nobody else.</summary>
    public Guid ByUserId { get; set; }
    public User? ByUser { get; set; }

    /// <summary>Who the score is about — denormalised from the award so
    /// per-user aggregates are one indexed filter, not a three-way join.</summary>
    public Guid OfUserId { get; set; }
    public User? OfUser { get; set; }

    /// <summary>1–5.</summary>
    public int Stars { get; set; }

    /// <summary>Optional public comment; the portal shows it wherever the score shows.</summary>
    public string? Comment { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
