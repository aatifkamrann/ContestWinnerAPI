-- Their live entries with the opportunity's state, and every dated milestone
-- on those with its claim — two result sets.
CREATE OR ALTER PROCEDURE [dbo].[Fit_Entries]
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    SELECT e.[Id], c.[Status] AS [OpportunityStatus]
    FROM [Entries] e
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    WHERE e.[FreelancerId] = @userId AND e.[Status] = 0 /* Active */;

    SELECT e.[Id] AS [EntryId], m.[DueUtc] AS [Due],
           (SELECT TOP (1) cp.[ClaimedAtUtc] FROM [Checkpoints] cp
            WHERE cp.[EntryId] = e.[Id] AND cp.[MilestoneId] = m.[Id]) AS [ClaimedAt]
    FROM [Entries] e
    JOIN [Milestones] m ON m.[OpportunityId] = e.[OpportunityId]
    WHERE e.[FreelancerId] = @userId AND e.[Status] = 0 /* Active */ AND m.[DueUtc] IS NOT NULL;
END
