-- Applications and entries counted per opportunity, and every award with its
-- winner — three result sets over the opportunity ids as JSON.
CREATE OR ALTER PROCEDURE [dbo].[Report_OpportunityFigures]
    @ids nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @opportunities TABLE ([Id] uniqueidentifier PRIMARY KEY);
    INSERT INTO @opportunities SELECT [Id] FROM OPENJSON(@ids) WITH ([Id] uniqueidentifier '$');

    SELECT a.[OpportunityId] AS [Key], COUNT(*) AS [Total],
           SUM(CASE WHEN a.[Status] = 0 /* UnderReview */ THEN 1 ELSE 0 END) AS [Waiting],
           SUM(CASE WHEN a.[Status] = 1 /* Selected */ THEN 1 ELSE 0 END) AS [Selected]
    FROM [Applications] a
    JOIN @opportunities ids ON ids.[Id] = a.[OpportunityId]
    GROUP BY a.[OpportunityId];

    SELECT e.[OpportunityId] AS [Key], COUNT(*) AS [Total],
           SUM(CASE WHEN e.[Status] = 1 /* Withdrawn */ THEN 1 ELSE 0 END) AS [Withdrawn],
           SUM(CASE WHEN e.[Status] = 2 /* Removed */ THEN 1 ELSE 0 END) AS [Removed]
    FROM [Entries] e
    JOIN @opportunities ids ON ids.[Id] = e.[OpportunityId]
    GROUP BY e.[OpportunityId];

    SELECT a.[OpportunityId], w.[DisplayName] AS [Winner], a.[AnnouncedAtUtc], a.[PaidAtUtc]
    FROM [Awards] a
    JOIN @opportunities ids ON ids.[Id] = a.[OpportunityId]
    JOIN [Entries] e ON e.[Id] = a.[EntryId]
    JOIN [Users] w ON w.[Id] = e.[FreelancerId]
    ORDER BY a.[AnnouncedAtUtc];
END
