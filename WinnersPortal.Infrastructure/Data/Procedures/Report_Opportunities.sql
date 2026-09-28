-- The opportunities report's rows: the opportunities with the client joined,
-- newest first, and their skills in order — two result sets. @scope is
-- 0 for every opportunity (an administrator), 1 for those @me posted, 2 for
-- those @me applied to or entered.
CREATE OR ALTER PROCEDURE [dbo].[Report_Opportunities]
    @scope int,
    @me uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    SELECT c.[Id], c.[Slug], c.[Title], c.[Status], c.[ClientId], k.[DisplayName] AS [ClientName], k.[Email] AS [ClientEmail],
           c.[Category], c.[Subcategory], c.[Delivery], c.[AwardAmount], c.[Currency], c.[MinMeritScore],
           c.[CreatedAtUtc], c.[PublishedAtUtc], c.[StartsAtUtc], c.[DeadlineUtc], c.[EntryCloseUtc], c.[CancelledAtUtc],
           c.[ActiveEntryCount], c.[MilestoneCount]
    FROM [Opportunities] c
    JOIN [Users] k ON k.[Id] = c.[ClientId]
    WHERE @scope = 0
       OR (@scope = 1 AND c.[ClientId] = @me)
       OR (@scope = 2 AND (EXISTS (SELECT 1 FROM [Applications] a WHERE a.[OpportunityId] = c.[Id] AND a.[FreelancerId] = @me)
                        OR EXISTS (SELECT 1 FROM [Entries] e WHERE e.[OpportunityId] = c.[Id] AND e.[FreelancerId] = @me)))
    ORDER BY c.[CreatedAtUtc] DESC, c.[Id] DESC
    OPTION (RECOMPILE);

    SELECT s.[OpportunityId], s.[Name]
    FROM [OpportunitySkills] s
    JOIN [Opportunities] c ON c.[Id] = s.[OpportunityId]
    WHERE @scope = 0
       OR (@scope = 1 AND c.[ClientId] = @me)
       OR (@scope = 2 AND (EXISTS (SELECT 1 FROM [Applications] a WHERE a.[OpportunityId] = c.[Id] AND a.[FreelancerId] = @me)
                        OR EXISTS (SELECT 1 FROM [Entries] e WHERE e.[OpportunityId] = c.[Id] AND e.[FreelancerId] = @me)))
    ORDER BY s.[OpportunityId], s.[Order]
    OPTION (RECOMPILE);
END
