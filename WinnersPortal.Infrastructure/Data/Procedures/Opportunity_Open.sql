-- Every opportunity open for entry now, as cards, and their skills — two result
-- sets. Open and still taking entries: the entry close, or the deadline
-- where none was set, is ahead.
CREATE OR ALTER PROCEDURE [dbo].[Opportunity_Open]
    @now datetimeoffset
AS
BEGIN
    SET NOCOUNT ON;

    SELECT c.[Id], c.[Slug], c.[Title], c.[AwardAmount], c.[Currency], c.[Category], c.[Subcategory], c.[MinMeritScore],
           LEFT(c.[BriefMarkdown], 240) AS [Brief240], c.[DeadlineUtc], c.[StartsAtUtc], c.[EntryCloseUtc], c.[PublishedAtUtc],
           c.[Status], c.[Delivery], c.[RequiresCompose], c.[ClientId], k.[DisplayName] AS [ClientName], k.[AvatarUpdatedAtUtc] AS [ClientAvatarAt],
           c.[ActiveEntryCount] AS [EntrantCount], c.[MilestoneCount], k.[AwardsPaidCount] AS [ClientAwardsPaid],
           k.[RatingCount] AS [ClientRatingCount],
           CASE WHEN k.[RatingCount] = 0 THEN NULL ELSE CAST(k.[RatingSum] AS float) / k.[RatingCount] END AS [ClientRatingAvg]
    FROM [Opportunities] c
    JOIN [Users] k ON k.[Id] = c.[ClientId]
    WHERE c.[Status] = 1 /* Open */ AND ISNULL(c.[EntryCloseUtc], c.[DeadlineUtc]) > @now;

    SELECT s.[OpportunityId], s.[Name]
    FROM [OpportunitySkills] s
    JOIN [Opportunities] c ON c.[Id] = s.[OpportunityId]
    WHERE c.[Status] = 1 /* Open */ AND ISNULL(c.[EntryCloseUtc], c.[DeadlineUtc]) > @now
    ORDER BY s.[OpportunityId], s.[Order];
END
