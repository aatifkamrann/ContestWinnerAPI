-- Gives back one counted call the provider never ran, to the portal
-- (@userId empty) or a member, on today's row only: a count that has
-- already rolled over is history. Never below zero.
CREATE OR ALTER PROCEDURE [dbo].[Ai_Refund]
    @day nvarchar(10),
    @userId uniqueidentifier
AS
BEGIN
    UPDATE [AiUsages] SET [Calls] = [Calls] - 1
    WHERE [Day] = @day AND [UserId] = @userId AND [Calls] > 0;
END
