-- One unit of a day's AI call allowance, for the portal (@userId empty)
-- or one member: counted in a single statement that only succeeds under
-- the limit, so two calls arriving together cannot both read the same
-- number and both pass. Answers 1 when the call may run and 0 when the
-- limit is reached, or is zero. The row is made on the day's first call;
-- when two first calls race, the loser counts on the row the winner made.
CREATE OR ALTER PROCEDURE [dbo].[Ai_Consume]
    @day nvarchar(10),
    @userId uniqueidentifier,
    @limit int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @allowed int = 0;
    -- The variable is set only on the row the update reaches: none under the limit, none set.
    UPDATE [AiUsages] SET [Calls] = [Calls] + 1, @allowed = 1
    WHERE [Day] = @day AND [UserId] = @userId AND [Calls] < @limit;
    IF @allowed = 0 AND @limit > 0 AND NOT EXISTS (SELECT 1 FROM [AiUsages] WHERE [Day] = @day AND [UserId] = @userId)
    BEGIN
        BEGIN TRY
            INSERT INTO [AiUsages] ([Day], [UserId], [Calls]) VALUES (@day, @userId, 1);
            SET @allowed = 1;
        END TRY
        BEGIN CATCH
            UPDATE [AiUsages] SET [Calls] = [Calls] + 1, @allowed = 1
            WHERE [Day] = @day AND [UserId] = @userId AND [Calls] < @limit;
        END CATCH
    END
    SELECT @allowed AS [Allowed];
END
