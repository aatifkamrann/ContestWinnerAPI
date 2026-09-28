-- The profile's ticks, its projects in order, and how many ways to be paid
-- — three result sets.
CREATE OR ALTER PROCEDURE [dbo].[Welcome_Profile]
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    SELECT p.[Headline], p.[Bio], p.[PrimaryCategory], p.[Availability],
           (SELECT COUNT(*) FROM [ProfileSkills] s WHERE s.[UserId] = p.[UserId]) AS [Skills]
    FROM [Profiles] p
    WHERE p.[UserId] = @userId AND p.[IsDeleted] = 0;

    SELECT x.[Title], x.[Category], x.[Description], x.[Outcome], x.[Role], x.[Tech]
    FROM [ProfileProjects] x
    JOIN [Profiles] p ON p.[UserId] = x.[UserId]
    WHERE x.[UserId] = @userId AND p.[IsDeleted] = 0
    ORDER BY x.[Order];

    SELECT COUNT(*) FROM [ProfilePayments] y WHERE y.[UserId] = @userId;
END
