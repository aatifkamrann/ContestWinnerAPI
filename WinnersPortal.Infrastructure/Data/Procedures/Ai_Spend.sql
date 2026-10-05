-- One answered call and its tokens on the day's row for a feature and a
-- model: what the usage panel adds up by feature and by model, and what a
-- daily budget is measured against. The row is made on the day's first
-- call for that feature and model; when two first calls race, the loser
-- adds to the row the winner made.
-- The sums are bracketed so the scripts' rule against joining a parameter
-- into text reads them as the arithmetic they are.
CREATE OR ALTER PROCEDURE [dbo].[Ai_Spend]
    @day nvarchar(10),
    @feature nvarchar(40),
    @provider nvarchar(20),
    @model nvarchar(80),
    @input bigint,
    @output bigint
AS
BEGIN
    UPDATE [AiSpends]
    SET [Calls] = [Calls] + 1, [InputTokens] = [InputTokens] + (@input), [OutputTokens] = [OutputTokens] + (@output)
    WHERE [Day] = @day AND [Feature] = @feature AND [Provider] = @provider AND [Model] = @model;
    IF NOT EXISTS (SELECT 1 FROM [AiSpends] WHERE [Day] = @day AND [Feature] = @feature AND [Provider] = @provider AND [Model] = @model)
    BEGIN
        BEGIN TRY
            INSERT INTO [AiSpends] ([Day], [Feature], [Provider], [Model], [Calls], [InputTokens], [OutputTokens])
            VALUES (@day, @feature, @provider, @model, 1, @input, @output);
        END TRY
        BEGIN CATCH
            UPDATE [AiSpends]
            SET [Calls] = [Calls] + 1, [InputTokens] = [InputTokens] + (@input), [OutputTokens] = [OutputTokens] + (@output)
            WHERE [Day] = @day AND [Feature] = @feature AND [Provider] = @provider AND [Model] = @model;
        END CATCH
    END
END
