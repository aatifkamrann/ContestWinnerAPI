using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Services.Chat;

/// <summary>
/// The SQL Server half of the administrator's view: <c>Chat_AdminList</c>
/// for the list, <c>Chat_AdminThread</c> for one conversation, the
/// members' own <c>Chat_Page</c> for its lines, <c>Chat_Reports</c> for its
/// reports, <c>Chat_ReportsOpen</c> for the dashboard, and
/// <c>Chat_ResolveReports</c> named where it is called.
/// </summary>
public sealed partial class ChatModerationService
{
    private static Task<List<ConversationRow>> ListSqlAsync(Sql sql, string? q, Guid? memberId, bool reported, Cursor? before, int take, CancellationToken ct) =>
        sql.QueryAsync<ConversationRow>(Procedures.ChatAdminList,
            new { take, memberId, q, beforeAt = before?.At, beforeId = before?.Id, reported }, ct);

    private static Task<ConversationRow?> OneSqlAsync(Sql sql, Guid entryId, CancellationToken ct) =>
        sql.SingleOrDefaultAsync<ConversationRow>(Procedures.ChatAdminThread, new { entryId }, ct);

    private static Task<List<ChatService.MessageRow>> PageSqlAsync(Sql sql, Guid entryId, Cursor? before, int take, CancellationToken ct) =>
        sql.QueryAsync<ChatService.MessageRow>(Procedures.ChatPage,
            new { entryId, take, beforeAt = before?.At, beforeId = before?.Id }, ct);

    private static Task<List<ReportRow>> ReportsSqlAsync(Sql sql, Guid entryId, CancellationToken ct) =>
        sql.QueryAsync<ReportRow>(Procedures.ChatReports, new { entryId }, ct);

    private static async Task<OpenReportsRow> OpenSqlAsync(Sql sql, CancellationToken ct) =>
        await sql.SingleOrDefaultAsync<OpenReportsRow>(Procedures.ChatReportsOpen, null, ct) ?? new OpenReportsRow(0, 0, null);
}
