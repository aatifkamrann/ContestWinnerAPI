-- Every day row from @fromDay on, for the usage panel's window and for
-- today's estimate against the daily budget: at most a month of rows, one
-- per day per feature per model.
CREATE OR ALTER PROCEDURE [dbo].[Ai_SpendSince]
    @fromDay nvarchar(10)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [Day], [Feature], [Provider], [Model], [Calls], [InputTokens], [OutputTokens]
    FROM [AiSpends]
    WHERE [Day] >= @fromDay
    ORDER BY [Day], [Feature], [Provider], [Model];
END
