-- A client's dashboard: seven result sets over their newest two hundred
-- opportunities. Statuses are the enums' numbers, named beside each.
CREATE OR ALTER PROCEDURE [dbo].[Dashboard_Client]
    @userId uniqueidentifier,
    @since datetimeoffset
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @opportunities TABLE ([Id] uniqueidentifier PRIMARY KEY);
    INSERT INTO @opportunities
        SELECT TOP (200) c.[Id] FROM [Opportunities] c
        WHERE c.[ClientId] = @userId
        ORDER BY c.[CreatedAtUtc] DESC;

    SELECT c.[Id], c.[Slug], c.[Title], c.[Status], c.[DeadlineUtc], c.[PublishedAtUtc], c.[AwardAmount], c.[Currency],
           (SELECT COUNT(*) FROM [Entries] e WHERE e.[OpportunityId] = c.[Id] AND e.[Status] = 0 /* Active */) AS [Entrants],
           (SELECT COUNT(*) FROM [Milestones] m WHERE m.[OpportunityId] = c.[Id]) AS [Milestones],
           (SELECT COUNT(*) FROM [Entries] e WHERE e.[OpportunityId] = c.[Id] AND e.[ProvisionStatus] = 2 /* Failed */) AS [RepoIssues]
    FROM [Opportunities] c JOIN @opportunities x ON x.[Id] = c.[Id]
    ORDER BY c.[CreatedAtUtc] DESC;

    SELECT e.[CreatedAtUtc]
    FROM [Entries] e JOIN @opportunities x ON x.[Id] = e.[OpportunityId]
    WHERE e.[CreatedAtUtc] >= @since;

    SELECT cp.[ClaimedAtUtc]
    FROM [Checkpoints] cp
    JOIN [Entries] e ON e.[Id] = cp.[EntryId]
    JOIN @opportunities x ON x.[Id] = e.[OpportunityId]
    WHERE cp.[ClaimedAtUtc] >= @since;

    SELECT e.[OpportunityId], COUNT(*) AS [Count]
    FROM [Checkpoints] cp
    JOIN [Entries] e ON e.[Id] = cp.[EntryId]
    JOIN @opportunities x ON x.[Id] = e.[OpportunityId]
    GROUP BY e.[OpportunityId];

    SELECT a.[Id], a.[Amount], a.[Currency], a.[AnnouncedAtUtc], a.[PaidAtUtc], a.[Handover],
           a.[HandoverNote], a.[TransferTargetLogin], a.[OpportunityId],
           c.[Slug], c.[Title], w.[DisplayName] AS [Winner]
    FROM [Awards] a
    JOIN [Opportunities] c ON c.[Id] = a.[OpportunityId]
    JOIN [Entries] e ON e.[Id] = a.[EntryId]
    JOIN [Users] w ON w.[Id] = e.[FreelancerId]
    WHERE c.[ClientId] = @userId;

    SELECT TOP (8) cp.[ClaimedAtUtc], cp.[Via], w.[DisplayName] AS [Who], m.[Title] AS [Milestone], m.[Order], c.[Slug]
    FROM [Checkpoints] cp
    JOIN [Entries] e ON e.[Id] = cp.[EntryId]
    JOIN @opportunities x ON x.[Id] = e.[OpportunityId]
    JOIN [Users] w ON w.[Id] = e.[FreelancerId]
    JOIN [Milestones] m ON m.[Id] = cp.[MilestoneId]
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    ORDER BY cp.[ClaimedAtUtc] DESC;

    SELECT TOP (10) e.[GithubUsername], c.[Slug], c.[Title]
    FROM [Entries] e
    JOIN @opportunities x ON x.[Id] = e.[OpportunityId]
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    WHERE e.[ProvisionStatus] = 2 /* Failed */;
END
