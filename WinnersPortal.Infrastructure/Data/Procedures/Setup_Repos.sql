-- Every repository an entry was given, so the GitHub setups' usage can be told.
CREATE OR ALTER PROCEDURE [dbo].[Setup_Repos]
AS
BEGIN
    SET NOCOUNT ON;
    SELECT e.[RepoFullName] FROM [Entries] e WHERE e.[RepoFullName] IS NOT NULL;
END
