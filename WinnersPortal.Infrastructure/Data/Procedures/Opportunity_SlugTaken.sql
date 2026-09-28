-- Whether another opportunity already has the slug; the one being renamed does
-- not count. 1 or 0.
CREATE OR ALTER PROCEDURE [dbo].[Opportunity_SlugTaken]
    @slug nvarchar(140),
    @exceptId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CASE WHEN EXISTS (SELECT 1 FROM [Opportunities] c
                             WHERE c.[Slug] = @slug AND (@exceptId IS NULL OR c.[Id] <> @exceptId))
                THEN 1 ELSE 0 END;
END
