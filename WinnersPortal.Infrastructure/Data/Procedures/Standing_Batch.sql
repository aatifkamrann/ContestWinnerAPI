-- The standing board's six batched reads as ten result sets in one round
-- trip: the opportunities and their milestones, the entries with their claims
-- and uploads, the entrants' measured profiles and skills, who connected
-- GitHub, their decided opportunities elsewhere, and their ratings. Two table
-- variables hold the ids, so the entrants are found by the batch itself.
CREATE OR ALTER PROCEDURE [dbo].[Standing_Batch]
    @ids nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @opportunities TABLE ([Id] uniqueidentifier PRIMARY KEY);
    INSERT INTO @opportunities SELECT [Id] FROM OPENJSON(@ids) WITH ([Id] uniqueidentifier '$');
    DECLARE @people TABLE ([Id] uniqueidentifier PRIMARY KEY);
    INSERT INTO @people
        SELECT DISTINCT e.[FreelancerId]
        FROM [Entries] e JOIN @opportunities x ON x.[Id] = e.[OpportunityId]
        WHERE e.[Status] = 0;

    SELECT c.[Id], c.[Title], c.[BriefMarkdown], c.[DeadlineUtc], c.[Delivery], c.[RequiresCompose]
    FROM [Opportunities] c JOIN @opportunities x ON x.[Id] = c.[Id];

    SELECT m.[OpportunityId], m.[Order], m.[Title], m.[Description], m.[DueUtc]
    FROM [Milestones] m JOIN @opportunities x ON x.[Id] = m.[OpportunityId]
    ORDER BY m.[OpportunityId], m.[Order];

    SELECT e.[Id], e.[OpportunityId], e.[FreelancerId], e.[CreatedAtUtc], e.[LastPushAtUtc], e.[Note],
           u.[DisplayName] AS [Name], e.[TreeFileCount], e.[TreeHasTests], e.[TreeHasReadme], e.[TreeHasCi], e.[TreeHasCompose]
    FROM [Entries] e
    JOIN [Users] u ON u.[Id] = e.[FreelancerId]
    JOIN @opportunities x ON x.[Id] = e.[OpportunityId]
    WHERE e.[Status] = 0;

    SELECT cp.[EntryId], m.[Order], cp.[ClaimedAtUtc], cp.[Id] AS [CheckpointId], cp.[BuildStatus], cp.[BuildFinishedAtUtc]
    FROM [Checkpoints] cp
    JOIN [Milestones] m ON m.[Id] = cp.[MilestoneId]
    JOIN [Entries] e ON e.[Id] = cp.[EntryId]
    JOIN @opportunities x ON x.[Id] = e.[OpportunityId]
    WHERE e.[Status] = 0;

    SELECT s.[EntryId], s.[FileName], s.[ContentType]
    FROM [Submissions] s
    JOIN [Entries] e ON e.[Id] = s.[EntryId]
    JOIN @opportunities x ON x.[Id] = e.[OpportunityId]
    WHERE e.[Status] = 0 AND s.[UploadedAtUtc] IS NOT NULL;

    SELECT p.[UserId], p.[Headline], p.[Bio], p.[Location], p.[HoursPerWeek], p.[YearsExperience],
           (SELECT COUNT(*) FROM [ProfileProjects] x WHERE x.[UserId] = p.[UserId]) AS [Projects],
           (SELECT COUNT(*) FROM [ProfileProjects] x
            WHERE x.[UserId] = p.[UserId] AND (x.[Url] IS NOT NULL OR x.[RepoUrl] IS NOT NULL)) AS [Linked]
    FROM [Profiles] p JOIN @people x ON x.[Id] = p.[UserId]
    WHERE p.[IsDeleted] = 0;

    SELECT s.[UserId], s.[Name]
    FROM [ProfileSkills] s JOIN @people x ON x.[Id] = s.[UserId]
    ORDER BY s.[UserId], s.[Order];

    SELECT u.[Id]
    FROM [Users] u JOIN @people x ON x.[Id] = u.[Id]
    WHERE u.[GithubLogin] IS NOT NULL;

    SELECT e.[FreelancerId],
           CAST(CASE WHEN EXISTS (SELECT 1 FROM [Awards] a WHERE a.[EntryId] = e.[Id]) THEN 1 ELSE 0 END AS bit) AS [Won]
    FROM [Entries] e
    JOIN @people x ON x.[Id] = e.[FreelancerId]
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    WHERE e.[Status] = 0 AND c.[Status] = 3
      AND NOT EXISTS (SELECT 1 FROM @opportunities y WHERE y.[Id] = e.[OpportunityId]);

    SELECT r.[OfUserId] AS [UserId], COUNT(*) AS [Count], SUM(r.[Stars]) AS [Sum]
    FROM [Ratings] r JOIN @people x ON x.[Id] = r.[OfUserId]
    GROUP BY r.[OfUserId];
END
