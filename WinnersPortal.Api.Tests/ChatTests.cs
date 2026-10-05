using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using WinnersPortal.Api.Live;
using WinnersPortal.Domain;
using WinnersPortal.Services.Activity;
using WinnersPortal.Services.Chat;
using Xunit;

namespace WinnersPortal.Api.Tests;

/// <summary>
/// The conversations' pure parts (Chat/ChatRules.cs): who is party to one,
/// when a line may still be added, what a line may hold, the excerpt the
/// list shows, and the names on the wire. The service is thin around
/// these, like the inbox around its rules.
/// </summary>
public class ChatRulesTests
{
    private static readonly Guid Freelancer = Guid.NewGuid();
    private static readonly Guid Client = Guid.NewGuid();
    private static readonly Guid Somebody = Guid.NewGuid();

    // ------------------------------------------------------------ party

    [Fact]
    public void The_entrant_and_the_client_are_party_and_nobody_else_is()
    {
        Assert.True(ChatRules.IsParty(Freelancer, Freelancer, Client));
        Assert.True(ChatRules.IsParty(Client, Freelancer, Client));
        // An administrator, another entrant, another client: not found, not forbidden.
        Assert.False(ChatRules.IsParty(Somebody, Freelancer, Client));
    }

    [Fact]
    public void The_other_side_is_the_client_to_the_entrant_and_the_entrant_to_the_client()
    {
        Assert.Equal(Client, ChatRules.Counterpart(Freelancer, Freelancer, Client));
        Assert.Equal(Freelancer, ChatRules.Counterpart(Client, Freelancer, Client));
    }

    // ------------------------------------------------------------- open

    // Open while the work is being done, reviewed, and after the award;
    // closed by a withdrawal, a removal, a deselection or a cancellation.
    [Theory]
    [InlineData(EntryStatus.Active, OpportunityStatus.Open, true)]
    [InlineData(EntryStatus.Active, OpportunityStatus.Reviewing, true)]
    [InlineData(EntryStatus.Active, OpportunityStatus.Awarded, true)]
    [InlineData(EntryStatus.Active, OpportunityStatus.Cancelled, false)]
    [InlineData(EntryStatus.Active, OpportunityStatus.Draft, false)]
    [InlineData(EntryStatus.Withdrawn, OpportunityStatus.Open, false)]
    [InlineData(EntryStatus.Removed, OpportunityStatus.Open, false)]
    [InlineData(EntryStatus.Deselected, OpportunityStatus.Open, false)]
    [InlineData(EntryStatus.Withdrawn, OpportunityStatus.Awarded, false)]
    public void A_line_may_be_added_only_to_an_active_entry_on_a_live_opportunity(EntryStatus entry, OpportunityStatus opportunity, bool open)
        => Assert.Equal(open, ChatRules.CanSend(entry, opportunity));

    [Fact]
    public void Every_status_has_a_send_decision()
    {
        // A new status must make a deliberate choice here, not inherit one.
        foreach (var entry in Enum.GetValues<EntryStatus>())
        foreach (var opportunity in Enum.GetValues<OpportunityStatus>())
            _ = ChatRules.CanSend(entry, opportunity);
        Assert.Equal(4, Enum.GetValues<EntryStatus>().Length);
        Assert.Equal(5, Enum.GetValues<OpportunityStatus>().Length);
    }

    // ------------------------------------------------------------- body

    [Fact]
    public void A_line_is_trimmed_and_neither_empty_nor_longer_than_the_column()
    {
        Assert.Equal(("Hello", null), ChatRules.Check("  Hello \n"));
        Assert.Equal((null, ChatRules.Empty), ChatRules.Check("   "));
        Assert.Equal((null, ChatRules.Empty), ChatRules.Check(null));
        Assert.Equal((null, ChatRules.TooLong), ChatRules.Check(new string('x', ChatMessage.MaxBodyLength + 1)));
        Assert.Null(ChatRules.Check(new string('x', ChatMessage.MaxBodyLength)).Error);
    }

    [Fact]
    public void The_excerpt_is_the_first_line_cut_to_length()
    {
        Assert.Equal("Can you start Monday?", ChatRules.Excerpt("Can you start Monday?\n\nThe brief says Tuesday."));
        var long_ = new string('a', 300);
        var excerpt = ChatRules.Excerpt(long_);
        Assert.Equal(ChatRules.ExcerptLength, excerpt.Length);
        Assert.EndsWith("…", excerpt);
        Assert.Equal("short", ChatRules.Excerpt("  short  "));
    }

    // ------------------------------------------------------------- wire

    [Fact]
    public void The_room_is_the_members_own_and_the_event_name_is_the_wire_contract()
    {
        var id = Guid.NewGuid();
        Assert.Equal("member:" + id.ToString("N"), ChatRules.UserGroup(id));
        Assert.NotEqual(ChatRules.UserGroup(id), ChatRules.UserGroup(Guid.NewGuid()));
        // The web client subscribes to this exact string; renaming it strands
        // every open tab silently — no error, just a dock that stopped moving.
        Assert.Equal("chatChanged", ChatRules.ChatChanged);
    }
}

/// <summary>
/// The administrator's read-only view of the conversations
/// (Chat/ChatModerationService.cs): admin-only routes, a reading that is
/// named in the activity log by the two people and never by a line, and a
/// list that says who and when but not what.
/// </summary>
public class ChatModerationTests
{
    private static readonly Guid Freelancer = Guid.NewGuid();
    private static readonly Guid Client = Guid.NewGuid();

    private static ChatModerationService.ConversationRow Row(int messages = 3, Guid? lastSender = null, EntryStatus entry = EntryStatus.Active, int reports = 0) =>
        new(Guid.NewGuid(), entry, "landing-page", "A landing page", OpportunityStatus.Open,
            Client, "Astrik", null, Freelancer, "Sam", null,
            messages, messages == 0 ? null : DateTimeOffset.UtcNow, messages == 0 ? null : lastSender ?? Freelancer, reports);

    [Fact]
    public void A_line_is_the_entrants_or_the_clients_by_who_sent_it()
    {
        Assert.Equal(Roles.Freelancer, ChatRules.SeatOf(Freelancer, Freelancer));
        Assert.Equal(Roles.Client, ChatRules.SeatOf(Client, Freelancer));
    }

    [Fact]
    public void The_list_says_who_and_when_and_never_what()
    {
        var view = ChatModerationService.View(Row(lastSender: Client));
        Assert.Equal("Astrik", view.Client.DisplayName);
        Assert.Equal("Sam", view.Freelancer.DisplayName);
        Assert.Equal(3, view.Messages);
        Assert.Equal(Roles.Client, view.LastFrom);
        Assert.True(view.Open);
        // No property of the list's row can carry a line.
        Assert.DoesNotContain(typeof(AdminConversationView).GetProperties(), p => p.Name is "Body" or "Excerpt" or "Last");
    }

    [Fact]
    public void A_conversation_with_nothing_said_has_no_last_side_and_a_closed_one_reads_closed()
    {
        var empty = ChatModerationService.View(Row(messages: 0));
        Assert.Null(empty.LastAtUtc);
        Assert.Null(empty.LastFrom);
        Assert.False(ChatModerationService.View(Row(entry: EntryStatus.Withdrawn)).Open);
    }

    [Fact]
    public void Reading_one_is_a_named_row_and_the_list_is_not()
    {
        Assert.True(ActivityNames.Worth("GET", "/api/admin/conversations/{id}"));
        Assert.Equal("Read a conversation", ActivityNames.Describe("GET", "/api/admin/conversations/{id}"));
        Assert.False(ActivityNames.Worth("GET", "/api/admin/conversations"));
    }

    [Fact]
    public void The_rows_subject_names_the_opportunity_and_both_people()
    {
        var subject = ChatRules.ModerationSubject("A landing page", "Astrik", "Sam");
        Assert.Equal("A landing page · Astrik and Sam", subject);
    }

    [Fact]
    public void Every_route_is_for_administrators_only_and_the_one_write_touches_reports_not_lines()
    {
        var actions = typeof(WinnersPortal.Api.Chat.ChatModerationController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.Equal(3, actions.Length);
        foreach (var action in actions)
        {
            var authorize = action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().SingleOrDefault();
            Assert.NotNull(authorize);
            Assert.Equal("admin", authorize!.Policy);
        }
        // Nothing an administrator does here can change what was said: the
        // reads are GETs, and the one POST marks reports reviewed.
        var posts = actions.Where(a => a.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).Length > 0).ToList();
        var post = Assert.Single(posts);
        var template = ((Microsoft.AspNetCore.Mvc.HttpPostAttribute)post.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).Single()).Template;
        Assert.EndsWith("/reports/resolve", template);
    }

    [Fact]
    public void A_row_says_how_many_reports_are_open()
    {
        Assert.Equal(2, ChatModerationService.View(Row(reports: 2)).OpenReports);
        Assert.Equal(0, ChatModerationService.View(Row()).OpenReports);
    }

    [Fact]
    public void A_report_says_which_side_made_it_and_why_in_words()
    {
        var report = new ChatModerationService.ReportRow(Guid.NewGuid(), Client, "Astrik", "spam", "Asked for my card number",
            DateTimeOffset.UtcNow, null, null, null);
        var view = ChatModerationService.View(report, Freelancer);
        Assert.Equal(Roles.Client, view.From);
        Assert.Equal("Spam, a scam or phishing", view.ReasonLabel);
        Assert.Null(view.ResolvedAtUtc);
    }

    [Fact]
    public void Resolving_is_a_named_row()
    {
        Assert.Equal("Marked a conversation's reports reviewed",
            ActivityNames.Describe("POST", "/api/admin/conversations/{id}/reports/resolve"));
    }
}

/// <summary>
/// A member's report of a conversation (ChatRules, ChatService.ReportAsync):
/// the reasons are a wire contract with the dialog, "something else" needs
/// words, a report needs something said and is one at a time per member.
/// </summary>
public class ChatReportRulesTests
{
    [Fact]
    public void The_reasons_are_the_dialogs_keys()
    {
        // The web dialog sends these exact keys (lib/chat.ts REPORT_REASONS).
        Assert.Equal(["abuse", "spam", "inappropriate", "other"], ChatRules.ReportReasons.Select(r => r.Key));
        Assert.All(ChatRules.ReportReasons, r => Assert.True(r.Key.Length <= ChatReport.MaxReasonLength));
        Assert.Equal("gone", ChatRules.ReasonLabel("gone"));
    }

    [Fact]
    public void A_reason_is_checked_and_details_are_trimmed_and_blank_as_none()
    {
        Assert.Equal(("abuse", null, null), ChatRules.CheckReport(" abuse ", "   "));
        Assert.Equal(("spam", "A link to a fake bank", null), ChatRules.CheckReport("spam", "  A link to a fake bank \n"));
        Assert.Equal(ChatRules.ReportReasonUnknown, ChatRules.CheckReport(null, "x").Error);
        Assert.Equal(ChatRules.ReportReasonUnknown, ChatRules.CheckReport("rude", "x").Error);
    }

    [Fact]
    public void Something_else_needs_words_and_no_report_is_longer_than_the_column()
    {
        Assert.Equal(ChatRules.ReportDetailsNeeded, ChatRules.CheckReport("other", " ").Error);
        Assert.Null(ChatRules.CheckReport("other", "They keep asking to meet").Error);
        Assert.Equal(ChatRules.ReportDetailsTooLong, ChatRules.CheckReport("abuse", new string('x', ChatReport.MaxDetailsLength + 1)).Error);
        Assert.Null(ChatRules.CheckReport("abuse", new string('x', ChatReport.MaxDetailsLength)).Error);
    }

    [Fact]
    public void A_report_needs_something_said_and_is_one_open_at_a_time()
    {
        Assert.Equal(ChatRules.ReportNothingSaid, ChatRules.ReportProblem(anythingSaid: false, openReportOfMine: false));
        Assert.Equal(ChatRules.ReportAlreadyOpen, ChatRules.ReportProblem(anythingSaid: true, openReportOfMine: true));
        Assert.Null(ChatRules.ReportProblem(anythingSaid: true, openReportOfMine: false));
    }

    [Fact]
    public void The_reviewers_note_is_optional_and_bounded()
    {
        Assert.Equal((null, null), ChatRules.CheckResolution("  "));
        Assert.Equal(("Warned the client", null), ChatRules.CheckResolution(" Warned the client "));
        Assert.Equal(ChatRules.ResolutionTooLong, ChatRules.CheckResolution(new string('x', ChatReport.MaxResolutionLength + 1)).Error);
    }

    [Fact]
    public void Reporting_is_a_named_row_and_member_words_never_name_an_administrator()
    {
        Assert.Equal("Reported a conversation", ActivityNames.Describe("POST", "/api/chat/threads/{id}/report"));
        // What a member reads: the refusals, and the email telling them it was reviewed.
        var memberText = new[] { ChatRules.ReportReasonUnknown, ChatRules.ReportDetailsNeeded, ChatRules.ReportDetailsTooLong,
            ChatRules.ReportNothingSaid, ChatRules.ReportAlreadyOpen }
            .Concat(ChatRules.ReportReasons.Select(r => r.Label))
            .Append(WinnersPortal.Services.Email.Emails.ChatReportReviewed(Guid.NewGuid(), "A logo", "Sam").TextBody);
        Assert.All(memberText, s => Assert.DoesNotContain("admin", s, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_administrators_email_carries_the_report_and_opens_the_conversation()
    {
        var entry = Guid.NewGuid();
        var email = WinnersPortal.Services.Email.Emails.ChatReported(entry, "A logo", "Astrik", "client", "Sam",
            "Spam, a scam or phishing", "Asked for my card number");
        Assert.Contains("Astrik, the client", email.TextBody);
        Assert.Contains("Asked for my card number", email.TextBody);
        Assert.Equal($"/admin/conversations?thread={entry}", email.ActionPath);
        Assert.DoesNotContain("In their words", WinnersPortal.Services.Email.Emails.ChatReported(entry, "A logo", "Astrik", "client", "Sam", "Spam", null).TextBody);
    }
}

/// <summary>
/// The conversations' bell (Live/LiveChat.cs), the board's twin: it rings
/// the member's own room with the one event and the entry, and never holds
/// up or fails the send that rang it.
/// </summary>
public class LiveChatTests
{
    private sealed class Proxy(Func<string, object?[], Task> send) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            send(method, args);
    }

    private sealed class Room(IClientProxy proxy, List<string> groups) : IHubClients
    {
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
        public IClientProxy Group(string groupName) { groups.Add(groupName); return proxy; }
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }

    private sealed class Hub(IHubClients clients) : IHubContext<ChatHub>
    {
        public IHubClients Clients { get; } = clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class Capture : ILogger<LiveChat>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    private static (LiveChat Live, List<string> Groups, Capture Log) Make(Func<string, object?[], Task> send)
    {
        var groups = new List<string>();
        var log = new Capture();
        return (new LiveChat(new Hub(new Room(new Proxy(send), groups)), log), groups, log);
    }

    [Fact]
    public async Task A_nudge_rings_the_members_room_with_the_one_event_and_the_entry()
    {
        var sent = new TaskCompletionSource<(string Method, object?[] Args)>();
        var (live, groups, _) = Make((m, a) => { sent.SetResult((m, a)); return Task.CompletedTask; });
        var member = Guid.NewGuid();
        var entry = Guid.NewGuid();

        await live.ChatChangedAsync(member, entry);

        var (method, args) = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ChatRules.UserGroup(member), Assert.Single(groups));
        Assert.Equal(ChatRules.ChatChanged, method);
        Assert.Equal(entry, Assert.Single(args));
    }

    [Fact]
    public async Task A_broadcast_that_never_finishes_does_not_hold_up_the_send_that_rang()
    {
        var never = new TaskCompletionSource();
        var (live, _, _) = Make((_, _) => never.Task);

        var ring = live.ChatChangedAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.True(ring.IsCompletedSuccessfully);
        never.SetResult();
    }

    [Fact]
    public async Task A_broadcast_that_fails_is_a_warning_and_nothing_more()
    {
        var (live, _, log) = Make((_, _) => throw new InvalidOperationException("redis is away"));

        await live.ChatChangedAsync(Guid.NewGuid(), Guid.NewGuid());
        // The ring runs on its own; give it a moment to log.
        for (var i = 0; i < 50 && log.Lines.Count == 0; i++) await Task.Delay(10);

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("refresh by themselves", line.Text);
    }
}
