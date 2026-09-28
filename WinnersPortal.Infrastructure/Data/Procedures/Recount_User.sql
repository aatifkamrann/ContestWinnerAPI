-- The card's trust line, recomputed from the source of truth in one
-- statement: awards paid as a client, ratings received in either role.
CREATE OR ALTER PROCEDURE [dbo].[Recount_User]
    @userId uniqueidentifier
AS
BEGIN
    UPDATE u SET
        [AwardsPaidCount] = (SELECT count(*) FROM [Awards] a
            JOIN [Opportunities] c ON c.[Id] = a.[OpportunityId]
            WHERE c.[ClientId] = u.[Id] AND a.[PaidAtUtc] IS NOT NULL),
        [RatingCount] = (SELECT count(*) FROM [Ratings] r WHERE r.[OfUserId] = u.[Id]),
        [RatingSum] = (SELECT coalesce(sum(r.[Stars]), 0) FROM [Ratings] r WHERE r.[OfUserId] = u.[Id])
    FROM [Users] u
    WHERE u.[Id] = @userId;
END
