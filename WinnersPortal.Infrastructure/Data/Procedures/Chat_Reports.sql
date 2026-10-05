-- Every report on one conversation, the open ones first and each group
-- newest first: who reported it, why and in their words, and, once it was
-- reviewed, when, by whom and the note they left. The administrator's
-- reader shows these above the lines.
CREATE OR ALTER PROCEDURE [dbo].[Chat_Reports]
    @entryId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    SELECT r.[Id], r.[ReporterId], p.[DisplayName] AS [ReporterName], r.[Reason], r.[Details], r.[CreatedAtUtc],
           r.[ResolvedAtUtc], v.[DisplayName] AS [ResolvedByName], r.[Resolution]
    FROM [ChatReports] r
    JOIN [Users] p ON p.[Id] = r.[ReporterId]
    LEFT JOIN [Users] v ON v.[Id] = r.[ResolvedById]
    WHERE r.[EntryId] = @entryId
    ORDER BY CASE WHEN r.[ResolvedAtUtc] IS NULL THEN 0 ELSE 1 END, r.[CreatedAtUtc] DESC, r.[Id] DESC;
END
