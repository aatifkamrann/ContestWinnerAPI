-- Every open opportunity's door: the merit floor, and the skills it names —
-- two result sets.
CREATE OR ALTER PROCEDURE [dbo].[ProfileReview_Open]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT c.[Id], c.[MinMeritScore]
    FROM [Opportunities] c
    WHERE c.[Status] = 1;

    SELECT s.[OpportunityId], s.[Name], s.[Key]
    FROM [OpportunitySkills] s
    JOIN [Opportunities] c ON c.[Id] = s.[OpportunityId]
    WHERE c.[Status] = 1;
END
