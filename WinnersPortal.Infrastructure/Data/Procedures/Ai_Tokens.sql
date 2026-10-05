-- Adds a call's tokens, as the provider counted them, to a day's row for
-- the portal (@userId empty) or one member — the row the count already
-- made. A row that is not there (the count rolled over at midnight while
-- the call ran) is made with the tokens and no call, so the day's bill is
-- never under-stated.
-- The sums are bracketed so the scripts' rule against joining a parameter
-- into text reads them as the arithmetic they are.
CREATE OR ALTER PROCEDURE [dbo].[Ai_Tokens]
    @day nvarchar(10),
    @userId uniqueidentifier,
    @input bigint,
    @output bigint
AS
BEGIN
    UPDATE [AiUsages] SET [InputTokens] = [InputTokens] + (@input), [OutputTokens] = [OutputTokens] + (@output)
    WHERE [Day] = @day AND [UserId] = @userId;
    IF NOT EXISTS (SELECT 1 FROM [AiUsages] WHERE [Day] = @day AND [UserId] = @userId)
    BEGIN
        BEGIN TRY
            INSERT INTO [AiUsages] ([Day], [UserId], [Calls], [InputTokens], [OutputTokens])
            VALUES (@day, @userId, 0, @input, @output);
        END TRY
        BEGIN CATCH
            UPDATE [AiUsages] SET [InputTokens] = [InputTokens] + (@input), [OutputTokens] = [OutputTokens] + (@output)
            WHERE [Day] = @day AND [UserId] = @userId;
        END CATCH
    END
END
