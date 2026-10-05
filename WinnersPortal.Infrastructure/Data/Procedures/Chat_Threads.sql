-- Every conversation a member is party to, newest first: the entries they
-- made or that were made into their opportunities, while active or once
-- something was said. The other side is picked per row, so one script
-- serves a client and an entrant alike; beside each, its last line and how
-- many of the other side's lines the member has not opened, and when they
-- reported it if that report is still open. Ordered by the
-- last line's time, or the entry's own where nothing was said yet.
CREATE OR ALTER PROCEDURE [dbo].[Chat_Threads]
    @userId uniqueidentifier,
    @take int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@take)
        e.[Id] AS [EntryId], e.[Status] AS [EntryStatus], e.[FreelancerId], c.[ClientId],
        c.[Slug], c.[Title], c.[Status] AS [OpportunityStatus],
        o.[Id] AS [OtherId], o.[DisplayName] AS [OtherName], o.[AvatarUpdatedAtUtc] AS [OtherAvatarUpdatedAtUtc],
        l.[Body] AS [LastBody], l.[CreatedAtUtc] AS [LastAtUtc], l.[SenderId] AS [LastSenderId],
        (SELECT COUNT(*) FROM [ChatMessages] u
         WHERE u.[EntryId] = e.[Id] AND u.[SenderId] <> @userId AND u.[ReadAtUtc] IS NULL) AS [Unread],
        (SELECT MAX(r.[CreatedAtUtc]) FROM [ChatReports] r
         WHERE r.[EntryId] = e.[Id] AND r.[ReporterId] = @userId AND r.[ResolvedAtUtc] IS NULL) AS [MyReportAtUtc]
    FROM [Entries] e
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    JOIN [Users] o ON o.[Id] = CASE WHEN e.[FreelancerId] = @userId THEN c.[ClientId] ELSE e.[FreelancerId] END
    OUTER APPLY (
        SELECT TOP (1) m.[Body], m.[CreatedAtUtc], m.[SenderId]
        FROM [ChatMessages] m
        WHERE m.[EntryId] = e.[Id]
        ORDER BY m.[CreatedAtUtc] DESC, m.[Id] DESC) l
    WHERE (e.[FreelancerId] = @userId OR c.[ClientId] = @userId)
      AND (e.[Status] = 0 OR EXISTS (SELECT 1 FROM [ChatMessages] a WHERE a.[EntryId] = e.[Id]))
    ORDER BY COALESCE(l.[CreatedAtUtc], e.[CreatedAtUtc]) DESC, e.[Id] DESC;
END
