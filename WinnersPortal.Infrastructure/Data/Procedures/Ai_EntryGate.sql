-- An entry into an opportunity of the viewer's: its state, and when it froze.
CREATE OR ALTER PROCEDURE [dbo].[Ai_EntryGate]
    @id uniqueidentifier,
    @clientId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT e.[Status], e.[FrozenAtUtc]
    FROM [Entries] e
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    WHERE e.[Id] = @id AND c.[ClientId] = @clientId;
END
