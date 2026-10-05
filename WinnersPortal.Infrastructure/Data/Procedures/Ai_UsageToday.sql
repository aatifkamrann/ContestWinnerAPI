-- Today's count for the portal (@userId empty) or one member: the number
-- the settings test and the status line show beside the limit.
CREATE OR ALTER PROCEDURE [dbo].[Ai_UsageToday]
    @day nvarchar(10),
    @userId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ISNULL((SELECT [Calls] FROM [AiUsages] WHERE [Day] = @day AND [UserId] = @userId), 0) AS [Calls];
END
