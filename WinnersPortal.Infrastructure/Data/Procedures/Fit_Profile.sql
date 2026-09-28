-- The profile, its skills in order and its projects in order — three
-- result sets.
CREATE OR ALTER PROCEDURE [dbo].[Fit_Profile]
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    SELECT p.[Headline], p.[Availability], p.[HoursPerWeek]
    FROM [Profiles] p
    WHERE p.[UserId] = @userId AND p.[IsDeleted] = 0;

    SELECT s.[Name], s.[Level], s.[Years]
    FROM [ProfileSkills] s
    JOIN [Profiles] p ON p.[UserId] = s.[UserId]
    WHERE s.[UserId] = @userId AND p.[IsDeleted] = 0
    ORDER BY s.[Order];

    SELECT x.[Category], x.[Title], x.[Description], x.[Outcome], x.[Role], x.[Tech]
    FROM [ProfileProjects] x
    JOIN [Profiles] p ON p.[UserId] = x.[UserId]
    WHERE x.[UserId] = @userId AND p.[IsDeleted] = 0
    ORDER BY x.[Order];
END
