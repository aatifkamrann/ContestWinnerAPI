-- Every provider and model the activity log's AI calls were made with, as
-- the rows name them (provider/model in the Subject), with how many calls
-- each and the last one's time: what the activity screen's Provider and
-- Model filters offer. A call recorded before rows carried their model has
-- no Subject and is not counted.
CREATE OR ALTER PROCEDURE [dbo].[Activity_AiModels]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [Subject], COUNT(*) AS [Calls], MAX([AtUtc]) AS [LastAtUtc]
    FROM [ActivityEvents]
    WHERE [Service] = N'ai' AND [Subject] IS NOT NULL
    GROUP BY [Subject];
END
