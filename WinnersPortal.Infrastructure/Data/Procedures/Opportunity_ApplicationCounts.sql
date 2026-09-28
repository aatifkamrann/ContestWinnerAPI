-- Applications per opportunity, whatever became of them; the ids as JSON.
CREATE OR ALTER PROCEDURE [dbo].[Opportunity_ApplicationCounts]
    @ids nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT a.[OpportunityId], COUNT(*) AS [Count]
    FROM [Applications] a
    JOIN OPENJSON(@ids) WITH ([Id] uniqueidentifier '$') ids ON ids.[Id] = a.[OpportunityId]
    GROUP BY a.[OpportunityId];
END
