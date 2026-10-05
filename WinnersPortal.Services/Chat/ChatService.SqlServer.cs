using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Opportunities;

namespace WinnersPortal.Services.Chat;

/// <summary>
/// The SQL Server half of the conversations: <c>Chat_Threads</c> for the
/// list, <c>Chat_Thread</c> for the one door every other call goes through,
/// <c>Chat_Page</c> for a page of lines, and <c>Chat_MarkRead</c> named
/// where it is called.
/// </summary>
public sealed partial class ChatService
{
    private static Task<List<ThreadRow>> ThreadsSqlAsync(Sql sql, Guid userId, int take, CancellationToken ct) =>
        sql.QueryAsync<ThreadRow>(Procedures.ChatThreads, new { userId, take }, ct);

    private static Task<ThreadRow?> ThreadSqlAsync(Sql sql, Guid entryId, Guid userId, CancellationToken ct) =>
        sql.SingleOrDefaultAsync<ThreadRow>(Procedures.ChatThread, new { entryId, userId }, ct);

    private static Task<List<MessageRow>> PageSqlAsync(Sql sql, Guid entryId, Cursor? before, int take, CancellationToken ct) =>
        sql.QueryAsync<MessageRow>(Procedures.ChatPage,
            new { entryId, take, beforeAt = before?.At, beforeId = before?.Id }, ct);
}
