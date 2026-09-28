-- One person's contested entries, wins, on-time and dated milestone counts,
-- and ratings — the record the merit score is computed from, one row.
CREATE OR ALTER PROCEDURE [dbo].[Merit_Record]
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        (SELECT COUNT(*) FROM [Entries] e
         WHERE e.[FreelancerId] = @userId AND e.[Status] = 0) AS [Entries],
        (SELECT COUNT(*) FROM [Awards] a
         JOIN [Entries] e ON e.[Id] = a.[EntryId]
         WHERE e.[FreelancerId] = @userId) AS [Wins],
        (SELECT COUNT(*) FROM [Checkpoints] cp
         JOIN [Entries] e ON e.[Id] = cp.[EntryId]
         JOIN [Milestones] m ON m.[Id] = cp.[MilestoneId]
         WHERE e.[FreelancerId] = @userId AND e.[Status] = 0
           AND m.[DueUtc] IS NOT NULL AND cp.[ClaimedAtUtc] <= m.[DueUtc]) AS [MilestonesOnTime],
        (SELECT COUNT(*) FROM [Entries] e
         JOIN [Milestones] m ON m.[OpportunityId] = e.[OpportunityId]
         WHERE e.[FreelancerId] = @userId AND e.[Status] = 0 AND m.[DueUtc] IS NOT NULL) AS [MilestonesDated],
        (SELECT COUNT(*) FROM [Ratings] r WHERE r.[OfUserId] = @userId) AS [RatingCount],
        (SELECT ISNULL(SUM(r.[Stars]), 0) FROM [Ratings] r WHERE r.[OfUserId] = @userId) AS [RatingSum];
END
