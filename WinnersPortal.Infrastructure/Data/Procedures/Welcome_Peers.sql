-- Which of the given members work mainly in a category; the ids as JSON.
CREATE OR ALTER PROCEDURE [dbo].[Welcome_Peers]
    @ids nvarchar(max),
    @categoryKey nvarchar(40)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT p.[UserId]
    FROM [Profiles] p
    JOIN OPENJSON(@ids) WITH ([Id] uniqueidentifier '$') ids ON ids.[Id] = p.[UserId]
    WHERE p.[PrimaryCategory] = @categoryKey AND p.[IsDeleted] = 0;
END
