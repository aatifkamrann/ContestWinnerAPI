-- Whether the opportunity is the client's. 1 or 0.
CREATE OR ALTER PROCEDURE [dbo].[Ai_OwnsOpportunity]
    @id uniqueidentifier,
    @clientId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CASE WHEN EXISTS (SELECT 1 FROM [Opportunities] c WHERE c.[Id] = @id AND c.[ClientId] = @clientId) THEN 1 ELSE 0 END;
END
