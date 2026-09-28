-- The profile, its skills in order, its projects' links and kinds, and how
-- many ways to be paid — four result sets.
CREATE OR ALTER PROCEDURE [dbo].[ProfileReview_Profile]
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    SELECT p.[Headline], p.[Bio], p.[Location], p.[PrimaryCategory], p.[SecondaryCategories],
           p.[Availability], p.[HoursPerWeek], p.[YearsExperience]
    FROM [Profiles] p
    WHERE p.[UserId] = @userId AND p.[IsDeleted] = 0;

    SELECT s.[Name], s.[Level], s.[Years]
    FROM [ProfileSkills] s
    JOIN [Profiles] p ON p.[UserId] = s.[UserId]
    WHERE s.[UserId] = @userId AND p.[IsDeleted] = 0
    ORDER BY s.[Order];

    SELECT x.[Category], x.[Url], x.[RepoUrl]
    FROM [ProfileProjects] x
    JOIN [Profiles] p ON p.[UserId] = x.[UserId]
    WHERE x.[UserId] = @userId AND p.[IsDeleted] = 0;

    SELECT COUNT(*) FROM [ProfilePayments] y WHERE y.[UserId] = @userId;
END
