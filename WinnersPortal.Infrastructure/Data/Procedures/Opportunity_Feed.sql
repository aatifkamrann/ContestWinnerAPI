-- One page of the feed: the ids under the order in force, then the cards in
-- that order, then their skills — two result sets. Every filter is a
-- parameter, null when it is not applied; @sort is 0 newest first, 1 ending
-- soonest first, 2 by award; the cursor is the last card of the page
-- before, under the same order. The words, when there are any, go through
-- the full-text index and the title as typed, found first into @hits so
-- FREETEXT never sees a null. Each page query is recompiled for its own
-- parameters, so a filter that is null costs nothing in the plan.
CREATE OR ALTER PROCEDURE [dbo].[Opportunity_Feed]
    @openOnly bit,
    @sort tinyint,
    @now datetimeoffset,
    @take int,
    @words nvarchar(4000) = NULL,
    @pattern nvarchar(4000) = NULL,
    @category nvarchar(40) = NULL,
    @subcategory nvarchar(40) = NULL,
    @cursorAt datetimeoffset = NULL,
    @cursorId uniqueidentifier = NULL,
    @cursorAmount decimal(12,2) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @hits TABLE ([Id] uniqueidentifier PRIMARY KEY);
    IF @words IS NOT NULL
        INSERT INTO @hits
            SELECT c.[Id]
            FROM [Opportunities] c
            WHERE FREETEXT(c.[Title], @words) OR FREETEXT(c.[BriefMarkdown], @words) OR c.[Title] LIKE @pattern ESCAPE '\';

    DECLARE @page TABLE ([Id] uniqueidentifier PRIMARY KEY, [N] int NOT NULL);
    IF @sort = 2
        INSERT INTO @page
            SELECT TOP (@take) c.[Id], ROW_NUMBER() OVER (ORDER BY c.[AwardAmount] DESC, c.[PublishedAtUtc] DESC, c.[Id] DESC)
            FROM [Opportunities] c
            WHERE c.[Status] <> 0 /* Draft */ AND c.[Status] <> 4 /* Cancelled */
              AND (@openOnly = 0 OR (c.[Status] = 1 /* Open */ AND ISNULL(c.[EntryCloseUtc], c.[DeadlineUtc]) > @now))
              AND (@words IS NULL OR c.[Id] IN (SELECT h.[Id] FROM @hits h))
              AND (@category IS NULL OR c.[Category] = @category)
              AND (@subcategory IS NULL OR c.[Subcategory] = @subcategory)
              AND (@cursorId IS NULL OR c.[AwardAmount] < @cursorAmount OR (c.[AwardAmount] = @cursorAmount
                   AND (c.[PublishedAtUtc] < @cursorAt OR (c.[PublishedAtUtc] = @cursorAt AND c.[Id] < @cursorId))))
            ORDER BY c.[AwardAmount] DESC, c.[PublishedAtUtc] DESC, c.[Id] DESC
            OPTION (RECOMPILE);
    ELSE IF @sort = 1
        INSERT INTO @page
            SELECT TOP (@take) c.[Id], ROW_NUMBER() OVER (ORDER BY c.[DeadlineUtc], c.[Id])
            FROM [Opportunities] c
            WHERE c.[Status] <> 0 /* Draft */ AND c.[Status] <> 4 /* Cancelled */
              AND (@openOnly = 0 OR (c.[Status] = 1 /* Open */ AND ISNULL(c.[EntryCloseUtc], c.[DeadlineUtc]) > @now))
              AND (@words IS NULL OR c.[Id] IN (SELECT h.[Id] FROM @hits h))
              AND (@category IS NULL OR c.[Category] = @category)
              AND (@subcategory IS NULL OR c.[Subcategory] = @subcategory)
              AND c.[DeadlineUtc] > @now
              AND (@cursorId IS NULL OR c.[DeadlineUtc] > @cursorAt OR (c.[DeadlineUtc] = @cursorAt AND c.[Id] > @cursorId))
            ORDER BY c.[DeadlineUtc], c.[Id]
            OPTION (RECOMPILE);
    ELSE
        INSERT INTO @page
            SELECT TOP (@take) c.[Id], ROW_NUMBER() OVER (ORDER BY c.[PublishedAtUtc] DESC, c.[Id] DESC)
            FROM [Opportunities] c
            WHERE c.[Status] <> 0 /* Draft */ AND c.[Status] <> 4 /* Cancelled */
              AND (@openOnly = 0 OR (c.[Status] = 1 /* Open */ AND ISNULL(c.[EntryCloseUtc], c.[DeadlineUtc]) > @now))
              AND (@words IS NULL OR c.[Id] IN (SELECT h.[Id] FROM @hits h))
              AND (@category IS NULL OR c.[Category] = @category)
              AND (@subcategory IS NULL OR c.[Subcategory] = @subcategory)
              AND (@cursorId IS NULL OR c.[PublishedAtUtc] < @cursorAt OR (c.[PublishedAtUtc] = @cursorAt AND c.[Id] < @cursorId))
            ORDER BY c.[PublishedAtUtc] DESC, c.[Id] DESC
            OPTION (RECOMPILE);

    SELECT c.[Id], c.[Slug], c.[Title], c.[AwardAmount], c.[Currency], c.[Category], c.[Subcategory], c.[MinMeritScore],
           LEFT(c.[BriefMarkdown], 240) AS [Brief240], c.[DeadlineUtc], c.[StartsAtUtc], c.[EntryCloseUtc], c.[PublishedAtUtc],
           c.[Status], c.[Delivery], c.[Kind], c.[RequiresCompose], c.[ClientId], k.[DisplayName] AS [ClientName], k.[AvatarUpdatedAtUtc] AS [ClientAvatarAt],
           c.[ActiveEntryCount] AS [EntrantCount], c.[MilestoneCount], k.[AwardsPaidCount] AS [ClientAwardsPaid],
           k.[RatingCount] AS [ClientRatingCount],
           CASE WHEN k.[RatingCount] = 0 THEN NULL ELSE CAST(k.[RatingSum] AS float) / k.[RatingCount] END AS [ClientRatingAvg]
    FROM @page p
    JOIN [Opportunities] c ON c.[Id] = p.[Id]
    JOIN [Users] k ON k.[Id] = c.[ClientId]
    ORDER BY p.[N];

    SELECT s.[OpportunityId], s.[Name]
    FROM [OpportunitySkills] s
    JOIN @page p ON p.[Id] = s.[OpportunityId]
    ORDER BY s.[OpportunityId], s.[Order];
END
