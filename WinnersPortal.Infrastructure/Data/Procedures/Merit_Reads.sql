-- Many people's merit at once, four result sets over the ids as JSON: the
-- written half of their profiles measured (counts, never the text beyond
-- the headline), who among them connected GitHub, every contested entry
-- with the counts the record is scored from, and their ratings summed.
CREATE OR ALTER PROCEDURE [dbo].[Merit_Reads]
    @ids nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @people TABLE ([Id] uniqueidentifier PRIMARY KEY);
    INSERT INTO @people SELECT [Id] FROM OPENJSON(@ids) WITH ([Id] uniqueidentifier '$');

    SELECT p.[UserId], p.[Headline], p.[Bio], p.[Location], p.[HoursPerWeek], p.[YearsExperience],
           (SELECT COUNT(*) FROM [ProfileSkills] s WHERE s.[UserId] = p.[UserId]) AS [Skills],
           (SELECT COUNT(*) FROM [ProfileProjects] x WHERE x.[UserId] = p.[UserId]) AS [Projects],
           (SELECT COUNT(*) FROM [ProfileProjects] x
            WHERE x.[UserId] = p.[UserId] AND (x.[Url] IS NOT NULL OR x.[RepoUrl] IS NOT NULL)) AS [Linked]
    FROM [Profiles] p
    JOIN @people ids ON ids.[Id] = p.[UserId]
    WHERE p.[IsDeleted] = 0;

    SELECT u.[Id]
    FROM [Users] u
    JOIN @people ids ON ids.[Id] = u.[Id]
    WHERE u.[GithubLogin] IS NOT NULL;

    SELECT e.[FreelancerId],
           (SELECT COUNT(*) FROM [Checkpoints] cp
            JOIN [Milestones] m ON m.[Id] = cp.[MilestoneId]
            WHERE cp.[EntryId] = e.[Id] AND m.[DueUtc] IS NOT NULL AND cp.[ClaimedAtUtc] <= m.[DueUtc]) AS [OnTime],
           (SELECT COUNT(*) FROM [Milestones] m
            WHERE m.[OpportunityId] = e.[OpportunityId] AND m.[DueUtc] IS NOT NULL) AS [Dated],
           CAST(CASE WHEN EXISTS (SELECT 1 FROM [Awards] a WHERE a.[EntryId] = e.[Id]) THEN 1 ELSE 0 END AS bit) AS [Won]
    FROM [Entries] e
    JOIN @people ids ON ids.[Id] = e.[FreelancerId]
    WHERE e.[Status] = 0;

    SELECT r.[OfUserId] AS [UserId], COUNT(*) AS [Count], SUM(r.[Stars]) AS [Sum]
    FROM [Ratings] r
    JOIN @people ids ON ids.[Id] = r.[OfUserId]
    GROUP BY r.[OfUserId];
END
