-- The administrator's dashboard: twelve result sets over the whole portal.
-- The two leader boards sum a count per row through CROSS APPLY — a grouped
-- sum over a subquery, which SQL Server refuses in the shape EF Core writes.
CREATE OR ALTER PROCEDURE [dbo].[Dashboard_Admin]
    @since datetimeoffset,
    @dayAgo datetimeoffset
AS
BEGIN
    SET NOCOUNT ON;

    SELECT u.[Role], COUNT(*) AS [Count]
    FROM [Users] u
    WHERE u.[ErasedAtUtc] IS NULL
    GROUP BY u.[Role];

    SELECT u.[Role], u.[CreatedAtUtc]
    FROM [Users] u
    WHERE u.[ErasedAtUtc] IS NULL AND u.[CreatedAtUtc] >= @since;

    SELECT c.[Status], COUNT(*) AS [Count]
    FROM [Opportunities] c
    GROUP BY c.[Status];

    SELECT c.[PublishedAtUtc]
    FROM [Opportunities] c
    WHERE c.[PublishedAtUtc] >= @since;

    SELECT e.[CreatedAtUtc]
    FROM [Entries] e
    WHERE e.[CreatedAtUtc] >= @since;

    SELECT cp.[ClaimedAtUtc]
    FROM [Checkpoints] cp
    WHERE cp.[ClaimedAtUtc] >= @since;

    SELECT e.[ProvisionStatus] AS [Status], COUNT(*) AS [Count]
    FROM [Entries] e
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    WHERE e.[Status] = 0 /* Active */ AND c.[Delivery] <> 2 /* Upload */
    GROUP BY e.[ProvisionStatus];

    SELECT a.[Amount], a.[Currency], a.[AnnouncedAtUtc], a.[PaidAtUtc], a.[Handover],
           a.[TransferTargetLogin], a.[HandoverNote],
           c.[Slug], c.[Title], k.[DisplayName] AS [Client], c.[ClientId],
           w.[DisplayName] AS [Winner], e.[FreelancerId] AS [WinnerId]
    FROM [Awards] a
    JOIN [Opportunities] c ON c.[Id] = a.[OpportunityId]
    JOIN [Users] k ON k.[Id] = c.[ClientId]
    JOIN [Entries] e ON e.[Id] = a.[EntryId]
    JOIN [Users] w ON w.[Id] = e.[FreelancerId];

    SELECT d.[Event], d.[HandledNote], d.[ReceivedAtUtc], d.[RepoFullName]
    FROM [WebhookDeliveries] d
    WHERE d.[ReceivedAtUtc] >= @dayAgo;

    SELECT TOP (10) e.[GithubUsername], e.[ProvisionNote], e.[ProvisionAttempts], c.[Slug], c.[Title]
    FROM [Entries] e
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    WHERE e.[ProvisionStatus] = 2 /* Failed */
    ORDER BY e.[ProvisionAttemptedAtUtc] DESC;

    SELECT TOP (8) c.[ClientId], k.[DisplayName] AS [Name], COUNT(*) AS [Opportunities],
           SUM(n.[Entrants]) AS [Entrants], MAX(c.[CreatedAtUtc]) AS [LastPosted]
    FROM [Opportunities] c
    JOIN [Users] k ON k.[Id] = c.[ClientId]
    CROSS APPLY (SELECT COUNT(*) AS [Entrants] FROM [Entries] e
                 WHERE e.[OpportunityId] = c.[Id] AND e.[Status] = 0 /* Active */) n
    GROUP BY c.[ClientId], k.[DisplayName]
    ORDER BY COUNT(*) DESC;

    SELECT TOP (8) e.[FreelancerId], w.[DisplayName] AS [Name], COUNT(*) AS [Entries],
           SUM(n.[Claims]) AS [Claims], SUM(e.[PushCount]) AS [Pushes]
    FROM [Entries] e
    JOIN [Users] w ON w.[Id] = e.[FreelancerId]
    CROSS APPLY (SELECT COUNT(*) AS [Claims] FROM [Checkpoints] cp WHERE cp.[EntryId] = e.[Id]) n
    WHERE e.[Status] = 0 /* Active */
    GROUP BY e.[FreelancerId], w.[DisplayName]
    ORDER BY COUNT(*) DESC;
END
