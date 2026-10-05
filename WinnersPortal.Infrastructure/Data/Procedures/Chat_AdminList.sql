-- The administrator's list of conversations, for moderation: every entry
-- with something said in it, newest line first, keyset-paged on (the last
-- line's time, the entry) from the last row of the page before, both null
-- for the first page. Narrowed to one member on either side, and to the
-- typed words as a LIKE pattern matched against the opportunity's title
-- and both people's names and addresses, never against what was said.
-- Who said the last line, and how many there are; not the line itself.
-- With @reported = 1, only conversations with a report still open; every
-- row says how many it has.
CREATE OR ALTER PROCEDURE [dbo].[Chat_AdminList]
    @take int,
    @memberId uniqueidentifier = NULL,
    @q nvarchar(4000) = NULL,
    @beforeAt datetimeoffset = NULL,
    @beforeId uniqueidentifier = NULL,
    @reported bit = 0
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@take)
        e.[Id] AS [EntryId], e.[Status] AS [EntryStatus], c.[Slug], c.[Title], c.[Status] AS [OpportunityStatus],
        k.[Id] AS [ClientId], k.[DisplayName] AS [ClientName], k.[AvatarUpdatedAtUtc] AS [ClientAvatarUpdatedAtUtc],
        f.[Id] AS [FreelancerId], f.[DisplayName] AS [FreelancerName], f.[AvatarUpdatedAtUtc] AS [FreelancerAvatarUpdatedAtUtc],
        n.[Messages], l.[CreatedAtUtc] AS [LastAtUtc], l.[SenderId] AS [LastSenderId],
        (SELECT COUNT(*) FROM [ChatReports] r WHERE r.[EntryId] = e.[Id] AND r.[ResolvedAtUtc] IS NULL) AS [OpenReports]
    FROM [Entries] e
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    JOIN [Users] k ON k.[Id] = c.[ClientId]
    JOIN [Users] f ON f.[Id] = e.[FreelancerId]
    CROSS APPLY (
        SELECT TOP (1) m.[CreatedAtUtc], m.[SenderId]
        FROM [ChatMessages] m
        WHERE m.[EntryId] = e.[Id]
        ORDER BY m.[CreatedAtUtc] DESC, m.[Id] DESC) l
    CROSS APPLY (
        SELECT COUNT(*) AS [Messages]
        FROM [ChatMessages] a
        WHERE a.[EntryId] = e.[Id]) n
    WHERE (@memberId IS NULL OR e.[FreelancerId] = @memberId OR c.[ClientId] = @memberId)
      AND (@q IS NULL OR
           (c.[Title] LIKE @q ESCAPE '\' OR k.[DisplayName] LIKE @q ESCAPE '\' OR k.[Email] LIKE @q ESCAPE '\'
            OR f.[DisplayName] LIKE @q ESCAPE '\' OR f.[Email] LIKE @q ESCAPE '\'))
      AND (@reported = 0 OR EXISTS (SELECT 1 FROM [ChatReports] o WHERE o.[EntryId] = e.[Id] AND o.[ResolvedAtUtc] IS NULL))
      AND (@beforeAt IS NULL OR l.[CreatedAtUtc] < @beforeAt OR (l.[CreatedAtUtc] = @beforeAt AND e.[Id] < @beforeId))
    ORDER BY l.[CreatedAtUtc] DESC, e.[Id] DESC
    OPTION (RECOMPILE);
END
