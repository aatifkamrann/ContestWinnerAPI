-- The members' scores on the days in the trend window, the floor up to but
-- not including today; the ids as JSON.
CREATE OR ALTER PROCEDURE [dbo].[Leaderboard_History]
    @ids nvarchar(max),
    @floor date,
    @today date
AS
BEGIN
    SET NOCOUNT ON;
    SELECT s.[UserId], s.[DayUtc], s.[Score]
    FROM [MeritSnapshots] s
    JOIN OPENJSON(@ids) WITH ([Id] uniqueidentifier '$') ids ON ids.[Id] = s.[UserId]
    WHERE s.[DayUtc] >= @floor AND s.[DayUtc] < @today;
END
