-- One third-party call's request and response, read when its row is
-- opened on the activity screen. Nothing for a row of any other kind.
CREATE OR ALTER PROCEDURE [dbo].[Activity_Exchange]
    @id bigint
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [Id], [Request], [Response]
    FROM [ActivityEvents]
    WHERE [Id] = @id AND [Kind] = N'external';
END
