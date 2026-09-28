-- An opportunity's status by its slug; no row when there is no such opportunity.
CREATE OR ALTER PROCEDURE [dbo].[Opportunity_Status]
    @slug nvarchar(140)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT c.[Status] FROM [Opportunities] c WHERE c.[Slug] = @slug;
END
