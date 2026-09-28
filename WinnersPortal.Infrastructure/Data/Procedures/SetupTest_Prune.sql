-- The tests of a kind whose setup is no longer listed; the listed ids as JSON.
CREATE OR ALTER PROCEDURE [dbo].[SetupTest_Prune]
    @kind nvarchar(16),
    @ids nvarchar(max)
AS
BEGIN
    DELETE FROM [SetupTests]
    WHERE [Kind] = @kind
      AND NOT EXISTS (SELECT 1 FROM OPENJSON(@ids) WITH ([Id] nvarchar(16) '$') ids WHERE ids.[Id] = [SetupTests].[SetupId]);
END
