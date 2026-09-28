-- The stored artifact for a feature and subject, the whole row into the
-- entity, as the LINQ reads it.
CREATE OR ALTER PROCEDURE [dbo].[Ai_Artifact]
    @feature int,
    @subjectId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT a.* FROM [AiArtifacts] a WHERE a.[Feature] = @feature AND a.[SubjectId] = @subjectId;
END
