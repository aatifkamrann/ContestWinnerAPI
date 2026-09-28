namespace WinnersPortal.Services.Leaderboard;

/// <summary>The public leaderboard: the podium, the table under it, and the pickers' options.</summary>
public sealed record LeaderboardResponse
{
    public required string Tab { get; init; }
    public required string By { get; init; }
    public required string? Region { get; init; }
    public required string? Category { get; init; }
    public required int TrendDays { get; init; }
    public required int RisingDays { get; init; }
    public required DateTimeOffset AsOfUtc { get; init; }
    public required int Total { get; init; }
    public required IEnumerable<PodiumPlace> Podium { get; init; }
    public required IEnumerable<LeaderboardRow> Rows { get; init; }
    public required List<PickerOption> Regions { get; init; }
    public required List<PickerOption> Categories { get; init; }
}

/// <summary>One of the top three on a board.</summary>
public sealed record PodiumPlace
{
    public required int? Rank { get; init; }
    public required Guid UserId { get; init; }
    public required string Name { get; init; }
    public required string? AvatarUrl { get; init; }
    public required string? Headline { get; init; }
    public required int Merit { get; init; }
    public required string Band { get; init; }
}

/// <summary>A member's row on the board.</summary>
public sealed record LeaderboardRow
{
    public required int? Rank { get; init; }
    public required Guid UserId { get; init; }
    public required string Name { get; init; }
    public required string? AvatarUrl { get; init; }
    public required string? Headline { get; init; }
    public required string? Location { get; init; }
    public required int Merit { get; init; }
    public required string Band { get; init; }
    public required double? Rating { get; init; }
    public required int RatingCount { get; init; }
    public required int? Delivery { get; init; }
    public required int DeliveryOf { get; init; }
    public required int Wins { get; init; }
    public required int? Trend { get; init; }
    public required DateTimeOffset JoinedAtUtc { get; init; }
}

/// <summary>A region or a kind of work a picker offers, with how many members it would list.</summary>
public sealed record PickerOption(string Key, string Label, int Count);

/// <summary>The talent screen: the member lists the filters leave, and the pickers' options.</summary>
public sealed record TalentResponse
{
    public required TalentFilters Filters { get; init; }
    public required int Matched { get; init; }
    public required int Total { get; init; }
    public required int TrendDays { get; init; }
    public required DateTimeOffset AsOfUtc { get; init; }
    public required IEnumerable<TalentCard> Top { get; init; }
    public required IEnumerable<TalentCard> Rising { get; init; }
    public required IEnumerable<TalentCard> Leaders { get; init; }
    public required IEnumerable<TalentCard> Champions { get; init; }
    public required List<PickerOption> Regions { get; init; }
    public required List<PickerOption> Categories { get; init; }
}

/// <summary>The filters the talent lists were read with.</summary>
public sealed record TalentFilters
{
    public required string? Category { get; init; }
    public required string? Region { get; init; }
    public required int? Merit { get; init; }
    public required string? Availability { get; init; }
    public required int? Experience { get; init; }
    public required int? Wins { get; init; }
}

/// <summary>One member as a card: the face, the name, the title and the figures every panel shows.</summary>
public sealed record TalentCard(
    Guid UserId,
    string Name,
    string? AvatarUrl,
    string? Headline,
    int Merit,
    string Band,
    bool Verified,
    double? Rating,
    int RatingCount,
    int? Delivery,
    int DeliveryOf,
    int Wins,
    int? Trend,
    /// <summary>The kind of work a Category Leader leads, and its label; null on every other panel.</summary>
    string? Category = null,
    string? CategoryLabel = null);
