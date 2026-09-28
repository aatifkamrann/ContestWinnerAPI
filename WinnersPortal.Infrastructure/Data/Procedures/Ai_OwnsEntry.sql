-- Whether the entry is into an opportunity of the client's. 1 or 0.
CREATE OR ALTER PROCEDURE [dbo].[Ai_OwnsEntry]
    @id uniqueidentifier,
    @clientId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CASE WHEN EXISTS (SELECT 1 FROM [Entries] e JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
                             WHERE e.[Id] = @id AND c.[ClientId] = @clientId) THEN 1 ELSE 0 END;
END
