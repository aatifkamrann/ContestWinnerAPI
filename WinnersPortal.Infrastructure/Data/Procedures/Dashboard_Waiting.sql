-- Applications still to decide on open opportunities — every client's, or one
-- client's when @clientId is given.
CREATE OR ALTER PROCEDURE [dbo].[Dashboard_Waiting]
    @clientId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT a.[OpportunityId], c.[Slug], c.[Title], a.[SubmittedAtUtc]
    FROM [Applications] a
    JOIN [Opportunities] c ON c.[Id] = a.[OpportunityId]
    WHERE c.[Status] = 1 /* Open */ AND a.[Status] = 0 /* UnderReview */
      AND (@clientId IS NULL OR c.[ClientId] = @clientId);
END
