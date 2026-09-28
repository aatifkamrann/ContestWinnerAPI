-- The members' profiles' place, zone, kinds of work (as their JSON),
-- availability and experience; the ids as JSON.
CREATE OR ALTER PROCEDURE [dbo].[Leaderboard_Profiles]
    @ids nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT p.[UserId], p.[Location], p.[TimeZone], p.[PrimaryCategory], p.[SecondaryCategories],
           p.[Availability], p.[YearsExperience]
    FROM [Profiles] p
    JOIN OPENJSON(@ids) WITH ([Id] uniqueidentifier '$') ids ON ids.[Id] = p.[UserId]
    WHERE p.[IsDeleted] = 0;
END
