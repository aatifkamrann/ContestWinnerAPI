-- One conversation from one member's seat — the door every other chat call
-- goes through: no row where the entry is not theirs, whatever its state.
-- The same columns as Chat_Threads, for one entry.
CREATE OR ALTER PROCEDURE [dbo].[Chat_Thread]
    @entryId uniqueidentifier,
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
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
    WHERE e.[Id] = @entryId
      AND (e.[FreelancerId] = @userId OR c.[ClientId] = @userId);
END
