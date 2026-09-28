-- A member's inbox: how many lines are unread and how many there are, then
-- one page newest first — two result sets. The page is keyset-paged on
-- (CreatedAtUtc, Id) from the last line of the page before, both null for
-- the first page; the caller asks for one more than it shows to learn
-- whether there is another.
CREATE OR ALTER PROCEDURE [dbo].[Inbox_Page]
    @userId uniqueidentifier,
    @take int,
    @beforeAt datetimeoffset = NULL,
    @beforeId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT SUM(CASE WHEN n.[ReadAtUtc] IS NULL THEN 1 ELSE 0 END) AS [Unread], COUNT(*) AS [Total]
    FROM [Notifications] n
    WHERE n.[UserId] = @userId;

    SELECT TOP (@take) n.[Id], n.[Kind], n.[Title], n.[Body], n.[Path], n.[CreatedAtUtc], n.[ReadAtUtc]
    FROM [Notifications] n
    WHERE n.[UserId] = @userId
      AND (@beforeAt IS NULL OR n.[CreatedAtUtc] < @beforeAt OR (n.[CreatedAtUtc] = @beforeAt AND n.[Id] < @beforeId))
    ORDER BY n.[CreatedAtUtc] DESC, n.[Id] DESC
    OPTION (RECOMPILE);
END
