using WinnersPortal.Domain;

namespace WinnersPortal.Services.Chat;

/// <summary>
/// The rules of the conversation between a client and an entrant, kept pure
/// so the tests can hold them still. Who is party to it, when a line may
/// still be added, what a line may hold, and the names on the wire — the
/// live hub's room per member and its one event, which like the board's
/// carries no data: it says "your conversations changed" and the browser
/// refetches.
/// </summary>
public static class ChatRules
{
    /// <summary>The one client-side event name. Changing it strands every open tab.</summary>
    public const string ChatChanged = "chatChanged";

    /// <summary>The Messages page and the dock ask for this many lines of a conversation at a time.</summary>
    public const int PageSize = 50;
    public const int MaxPageSize = 100;

    /// <summary>The dock and the page list this many conversations, newest first.</summary>
    public const int MaxThreads = 200;

    /// <summary>The lead line shown under a conversation's name: this much of its last message.</summary>
    public const int ExcerptLength = 120;

    public const string Empty = "Write something first.";
    public static readonly string TooLong =
        $"A message is at most {ChatMessage.MaxBodyLength:N0} characters — send it in two.";
    public const string Closed =
        "This conversation is closed: the entry is no longer active, so no more can be sent. What was said stays readable.";

    /// <summary>
    /// SignalR group per member, keyed by account id: the room a sign-in's
    /// every tab sits in, rung whenever a conversation of theirs changes.
    /// </summary>
    public static string UserGroup(Guid userId) => "member:" + userId.ToString("N");

    /// <summary>
    /// Whether the viewer may take part in a conversation: they are the
    /// entrant or the opportunity's client, and nobody else — not another
    /// entrant. An administrator reads conversations for moderation from
    /// their own door (<see cref="ChatModerationService"/>), read-only and
    /// each reading logged, and never sits in one.
    /// </summary>
    public static bool IsParty(Guid viewerId, Guid freelancerId, Guid clientId) =>
        viewerId == freelancerId || viewerId == clientId;

    // ------------------------------------------------------------ reports

    /// <summary>
    /// Why a member reports a conversation, keyed as stored. The key is the
    /// wire contract with the report dialog; the label is what the
    /// administrator's screen and email read.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Label)> ReportReasons =
    [
        ("abuse", "Harassment, threats or abuse"),
        ("spam", "Spam, a scam or phishing"),
        ("inappropriate", "Inappropriate or offensive content"),
        ("other", "Something else"),
    ];

    public const string ReportReasonUnknown = "Choose why you are reporting this conversation.";
    public const string ReportDetailsNeeded = "Say in a few words what is wrong.";
    public static readonly string ReportDetailsTooLong =
        $"Keep it to {ChatReport.MaxDetailsLength:N0} characters.";
    public const string ReportNothingSaid = "Nothing has been said in this conversation yet, so there is nothing to report.";
    public const string ReportAlreadyOpen =
        "You have already reported this conversation, and it is being reviewed. You can report it again once that review is done.";
    public static readonly string ResolutionTooLong =
        $"Keep the note to {ChatReport.MaxResolutionLength:N0} characters.";
    public const string NothingToResolve = "This conversation has no open report.";

    /// <summary>The label for a reason key, or the key itself for one no longer offered.</summary>
    public static string ReasonLabel(string key) =>
        ReportReasons.FirstOrDefault(r => r.Key == key).Label ?? key;

    /// <summary>
    /// A report as stored — reason checked, details trimmed and blank as
    /// none — or the sentence that refuses it. "Something else" says nothing
    /// without the words, so it needs them; the others may stand alone.
    /// </summary>
    public static (string? Reason, string? Details, string? Error) CheckReport(string? reason, string? details)
    {
        var key = reason?.Trim() ?? "";
        if (!ReportReasons.Any(r => r.Key == key)) return (null, null, ReportReasonUnknown);
        var text = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        if (text is null && key == "other") return (null, null, ReportDetailsNeeded);
        if (text is { Length: > ChatReport.MaxDetailsLength }) return (null, null, ReportDetailsTooLong);
        return (key, text, null);
    }

    /// <summary>The reviewer's note as stored, blank as none, or the sentence that refuses it.</summary>
    public static (string? Note, string? Error) CheckResolution(string? note)
    {
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return text is { Length: > ChatReport.MaxResolutionLength } ? (null, ResolutionTooLong) : (text, null);
    }

    /// <summary>
    /// Whether the caller may report: something has been said, and they have
    /// no report on it still open. A closed conversation can be reported —
    /// what was said in it stays, and may be the reason it closed.
    /// </summary>
    public static string? ReportProblem(bool anythingSaid, bool openReportOfMine) =>
        !anythingSaid ? ReportNothingSaid
        : openReportOfMine ? ReportAlreadyOpen
        : null;

    /// <summary>The administrator's list of conversations, this many at a time.</summary>
    public const int AdminPageSize = 50;

    /// <summary>Which side said a line, from nobody's seat: the entrant or the client.</summary>
    public static string SeatOf(Guid senderId, Guid freelancerId) =>
        senderId == freelancerId ? Roles.Freelancer : Roles.Client;

    /// <summary>
    /// What the activity row of an administrator's reading names: the
    /// opportunity and the two people, so the log answers "whose
    /// conversation was read" without the path being opened. Names, never
    /// a line.
    /// </summary>
    public static string ModerationSubject(string title, string clientName, string freelancerName) =>
        $"{title} · {clientName} and {freelancerName}";

    /// <summary>The other side of the conversation, from the viewer's seat.</summary>
    public static Guid Counterpart(Guid viewerId, Guid freelancerId, Guid clientId) =>
        viewerId == freelancerId ? clientId : freelancerId;

    /// <summary>
    /// Whether a line may still be added. An active entry on a published
    /// opportunity that was not cancelled: while the work is being done,
    /// reviewed, and after the award — the winner and the client have a
    /// handover to talk through, the others a verdict to ask about. A
    /// withdrawn, removed or deselected entry closes its conversation,
    /// and so does a cancellation; what was said stays readable to both.
    /// </summary>
    public static bool CanSend(EntryStatus entry, OpportunityStatus opportunity) =>
        entry == EntryStatus.Active
        && opportunity is OpportunityStatus.Open or OpportunityStatus.Reviewing or OpportunityStatus.Awarded;

    /// <summary>The body as stored — trimmed — or the sentence that refuses it.</summary>
    public static (string? Body, string? Error) Check(string? body)
    {
        var text = body?.Trim() ?? "";
        if (text.Length == 0) return (null, Empty);
        if (text.Length > ChatMessage.MaxBodyLength) return (null, TooLong);
        return (text, null);
    }

    /// <summary>
    /// The first line of a message, cut to <see cref="ExcerptLength"/>, for
    /// the list of conversations: enough to know what it was about.
    /// </summary>
    public static string Excerpt(string body)
    {
        var line = body.AsSpan().Trim();
        var newline = line.IndexOfAny('\r', '\n');
        if (newline >= 0) line = line[..newline].TrimEnd();
        return line.Length <= ExcerptLength ? line.ToString() : string.Concat(line[..(ExcerptLength - 1)].TrimEnd(), "…");
    }
}
