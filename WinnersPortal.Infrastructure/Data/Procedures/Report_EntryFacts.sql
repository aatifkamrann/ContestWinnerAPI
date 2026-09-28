-- The milestones of the entries' opportunities, the applications the entries
-- came from, their awards and the ratings on those — four result sets, over
-- the entry ids and the opportunity ids, each as JSON.
CREATE OR ALTER PROCEDURE [dbo].[Report_EntryFacts]
    @ids nvarchar(max),
    @opportunities nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @entries TABLE ([Id] uniqueidentifier PRIMARY KEY);
    INSERT INTO @entries SELECT [Id] FROM OPENJSON(@ids) WITH ([Id] uniqueidentifier '$');

    SELECT m.[OpportunityId], m.[Order], m.[DueUtc]
    FROM [Milestones] m
    JOIN OPENJSON(@opportunities) WITH ([Id] uniqueidentifier '$') cs ON cs.[Id] = m.[OpportunityId];

    SELECT a.[EntryId], a.[SubmittedAtUtc], a.[DecidedAtUtc], a.[MatchAtSubmit]
    FROM [Applications] a
    JOIN @entries ids ON ids.[Id] = a.[EntryId];

    SELECT a.[Id], a.[EntryId], a.[AnnouncedAtUtc], a.[PaidAtUtc], a.[Handover]
    FROM [Awards] a
    JOIN @entries ids ON ids.[Id] = a.[EntryId];

    SELECT r.[AwardId], r.[OfUserId], r.[Stars]
    FROM [Ratings] r
    JOIN [Awards] a ON a.[Id] = r.[AwardId]
    JOIN @entries ids ON ids.[Id] = a.[EntryId];
END
