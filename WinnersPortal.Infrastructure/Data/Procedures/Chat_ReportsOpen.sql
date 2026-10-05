-- The reports nobody has reviewed yet, portal-wide: how many conversations
-- they cover, how many there are, and when the oldest was made — the
-- dashboard's attention line.
CREATE OR ALTER PROCEDURE [dbo].[Chat_ReportsOpen]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT COUNT(DISTINCT r.[EntryId]) AS [Conversations], COUNT(*) AS [Reports], MIN(r.[CreatedAtUtc]) AS [OldestUtc]
    FROM [ChatReports] r
    WHERE r.[ResolvedAtUtc] IS NULL;
END
