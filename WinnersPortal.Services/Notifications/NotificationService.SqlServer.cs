using Dapper;
using WinnersPortal.Infrastructure.Data;

namespace WinnersPortal.Services.Notifications;

/// <summary>
/// The SQL Server half of the inbox: one call to <c>Inbox_Page</c> brings
/// the counts and the page back together; the marks and the sweep are
/// <c>Inbox_MarkOne</c>, <c>Inbox_MarkAll</c> and <c>Inbox_Sweep</c>,
/// named where they are called.
/// </summary>
public sealed partial class NotificationService
{
    private static Task<InboxPage> InboxSqlAsync(Sql sql, Guid userId, Opportunities.Cursor? before, int take, CancellationToken ct) =>
        sql.MultipleAsync(Procedures.InboxPage,
            new { userId, take, beforeAt = before?.At, beforeId = before?.Id },
            async grid =>
            {
                var counts = await grid.ReadSingleAsync<InboxCounts>();
                var rows = (await grid.ReadAsync<InboxRow>()).AsList();
                return new InboxPage(counts.Unread ?? 0, counts.Total, rows);
            }, ct);

    /// <summary>SUM over no rows is null; COUNT is nought.</summary>
    private sealed record InboxCounts(int? Unread, int Total);
}
