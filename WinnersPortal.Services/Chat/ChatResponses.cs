namespace WinnersPortal.Services.Chat;

/// <summary>Every conversation the caller is party to, newest first, and how many lines in all await them.</summary>
public sealed record ChatThreadsResponse
{
    public required IEnumerable<ChatThreadView> Items { get; init; }
    public required int Unread { get; init; }
}

/// <summary>One conversation from the caller's seat: whose it is, where it stands, and what came last.</summary>
public sealed record ChatThreadView
{
    /// <summary>The entry the conversation belongs to — its id is the conversation's.</summary>
    public required Guid EntryId { get; init; }
    public required ChatOpportunityView Opportunity { get; init; }
    /// <summary>The other side: the client to an entrant, the entrant to the client.</summary>
    public required ChatPartyView Other { get; init; }
    /// <summary>The caller's seat: client or freelancer.</summary>
    public required string Seat { get; init; }
    /// <summary>active, withdrawn, removed or deselected — why a closed conversation is closed.</summary>
    public required string EntryStatus { get; init; }
    /// <summary>Whether a line may still be added; false reads as closed, with what was said kept.</summary>
    public required bool CanSend { get; init; }
    public required ChatLastLine? Last { get; init; }
    /// <summary>Lines from the other side the caller has not opened yet.</summary>
    public required int Unread { get; init; }
    /// <summary>When the caller reported it, while that report is still open; null otherwise.</summary>
    public required DateTimeOffset? ReportedAtUtc { get; init; }
}

public sealed record ChatOpportunityView
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
}

public sealed record ChatPartyView
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public required string? AvatarUrl { get; init; }
}

/// <summary>The last line of a conversation, cut short: enough to know what it was about and who said it.</summary>
public sealed record ChatLastLine
{
    public required string Excerpt { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
    public required bool Mine { get; init; }
}

/// <summary>One page of a conversation, newest first, with the conversation it belongs to.</summary>
public sealed record ChatThreadResponse
{
    public required ChatThreadView Thread { get; init; }
    public required IEnumerable<ChatMessageView> Items { get; init; }
    /// <summary>The cursor for the page before this one; null on the oldest.</summary>
    public required string? NextCursor { get; init; }
}

/// <summary>One line: who said it, when, and whether the other side has read it.</summary>
public sealed record ChatMessageView
{
    public required Guid Id { get; init; }
    public required string Body { get; init; }
    public required Guid SenderId { get; init; }
    public required bool Mine { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset? ReadAtUtc { get; init; }
}

/// <summary>A conversation marked read: how many of the other side's lines it covered.</summary>
public sealed record ChatReadResponse
{
    public required int Changed { get; init; }
}

public sealed record SendMessageRequest(string? Body);

/// <summary>A conversation reported: one of ChatRules.ReportReasons' keys, and the reporter's own words.</summary>
public sealed record ReportConversationRequest(string? Reason, string? Details);

public sealed record ChatReportResponse
{
    public required DateTimeOffset ReportedAtUtc { get; init; }
}

// ------------------------------------------------- the moderator's view

/// <summary>A page of conversations as an administrator lists them, newest line first.</summary>
public sealed record AdminConversationsResponse
{
    public required IEnumerable<AdminConversationView> Items { get; init; }
    /// <summary>The cursor for the next page; null on the last.</summary>
    public required string? NextCursor { get; init; }
}

/// <summary>
/// One conversation from nobody's seat: both people, the opportunity, how
/// many lines and when the last was said — never what was said.
/// </summary>
public sealed record AdminConversationView
{
    public required Guid EntryId { get; init; }
    public required ChatOpportunityView Opportunity { get; init; }
    public required ChatPartyView Client { get; init; }
    public required ChatPartyView Freelancer { get; init; }
    /// <summary>active, withdrawn, removed or deselected.</summary>
    public required string EntryStatus { get; init; }
    /// <summary>Whether the two can still add to it.</summary>
    public required bool Open { get; init; }
    public required int Messages { get; init; }
    public required DateTimeOffset? LastAtUtc { get; init; }
    /// <summary>client or freelancer: who said the last line; null where nothing was said.</summary>
    public required string? LastFrom { get; init; }
    /// <summary>Reports on it nobody has reviewed yet.</summary>
    public required int OpenReports { get; init; }
}

/// <summary>One page of a conversation as an administrator reads it, newest first.</summary>
public sealed record AdminConversationResponse
{
    public required AdminConversationView Conversation { get; init; }
    /// <summary>Every report on it, open ones first; on the first page only.</summary>
    public required IEnumerable<AdminReportView> Reports { get; init; }
    public required IEnumerable<AdminMessageView> Items { get; init; }
    /// <summary>The cursor for the page before this one; null on the oldest.</summary>
    public required string? NextCursor { get; init; }
}

/// <summary>One line, and which side said it.</summary>
public sealed record AdminMessageView
{
    public required Guid Id { get; init; }
    public required string Body { get; init; }
    /// <summary>client or freelancer.</summary>
    public required string From { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    /// <summary>When the other side opened it; an administrator's reading never sets it.</summary>
    public required DateTimeOffset? ReadAtUtc { get; init; }
}

/// <summary>A member's report of a conversation, and what became of it.</summary>
public sealed record AdminReportView
{
    public required Guid Id { get; init; }
    /// <summary>client or freelancer: which side reported it.</summary>
    public required string From { get; init; }
    public required string ReporterName { get; init; }
    public required string Reason { get; init; }
    public required string ReasonLabel { get; init; }
    public required string? Details { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    /// <summary>When it was marked reviewed; null while open.</summary>
    public required DateTimeOffset? ResolvedAtUtc { get; init; }
    public required string? ResolvedByName { get; init; }
    public required string? Resolution { get; init; }
}

/// <summary>A conversation's open reports marked reviewed, with an optional note.</summary>
public sealed record ResolveReportsRequest(string? Note);

public sealed record ResolveReportsResponse
{
    public required int Resolved { get; init; }
}
