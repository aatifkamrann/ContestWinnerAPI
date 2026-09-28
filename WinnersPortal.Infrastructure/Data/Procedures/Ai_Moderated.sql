-- An opportunity of the viewer's (or any, for an administrator): its state, and
-- whether anyone is competing.
CREATE OR ALTER PROCEDURE [dbo].[Ai_Moderated]
    @id uniqueidentifier,
    @isAdmin bit,
    @viewerId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT c.[Status],
           CAST(CASE WHEN EXISTS (SELECT 1 FROM [Entries] e WHERE e.[OpportunityId] = c.[Id] AND e.[Status] = 0 /* Active */)
                THEN 1 ELSE 0 END AS bit) AS [HasEntries]
    FROM [Opportunities] c
    WHERE c.[Id] = @id AND (@isAdmin = 1 OR c.[ClientId] = @viewerId);
END
