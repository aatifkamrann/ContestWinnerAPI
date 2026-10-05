-- One conversation as the administrator reads it: the same columns as
-- Chat_AdminList, for any entry, whether or not anything was said in it
-- yet. The lines themselves come from Chat_Page.
CREATE OR ALTER PROCEDURE [dbo].[Chat_AdminThread]
    @entryId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        e.[Id] AS [EntryId], e.[Status] AS [EntryStatus], c.[Slug], c.[Title], c.[Status] AS [OpportunityStatus],
        k.[Id] AS [ClientId], k.[DisplayName] AS [ClientName], k.[AvatarUpdatedAtUtc] AS [ClientAvatarUpdatedAtUtc],
        f.[Id] AS [FreelancerId], f.[DisplayName] AS [FreelancerName], f.[AvatarUpdatedAtUtc] AS [FreelancerAvatarUpdatedAtUtc],
        (SELECT COUNT(*) FROM [ChatMessages] a WHERE a.[EntryId] = e.[Id]) AS [Messages],
        l.[CreatedAtUtc] AS [LastAtUtc], l.[SenderId] AS [LastSenderId],
        (SELECT COUNT(*) FROM [ChatReports] r WHERE r.[EntryId] = e.[Id] AND r.[ResolvedAtUtc] IS NULL) AS [OpenReports]
    FROM [Entries] e
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    JOIN [Users] k ON k.[Id] = c.[ClientId]
    JOIN [Users] f ON f.[Id] = e.[FreelancerId]
    OUTER APPLY (
        SELECT TOP (1) m.[CreatedAtUtc], m.[SenderId]
        FROM [ChatMessages] m
        WHERE m.[EntryId] = e.[Id]
        ORDER BY m.[CreatedAtUtc] DESC, m.[Id] DESC) l
    WHERE e.[Id] = @entryId;
END
