-- The applications report's rows: every application with its applicant,
-- opportunity and client, newest first; the advantages come as their JSON.
-- @scope is 0 for every application (an administrator), 1 for those on
-- opportunities @me posted, 2 for those @me made.
CREATE OR ALTER PROCEDURE [dbo].[Report_Applications]
    @scope int,
    @me uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT a.[Id], a.[Status], a.[FreelancerId], f.[DisplayName] AS [FreelancerName], f.[Email] AS [FreelancerEmail],
           a.[OpportunityId], c.[Slug] AS [OpportunitySlug], c.[Title] AS [OpportunityTitle], c.[Status] AS [OpportunityStatus],
           c.[ClientId], k.[DisplayName] AS [ClientName], c.[Category], c.[Subcategory], c.[AwardAmount], c.[Currency],
           c.[MinMeritScore], c.[DeadlineUtc], c.[EntryCloseUtc], a.[SubmittedAtUtc], a.[DecidedAtUtc], a.[EntryId],
           a.[MatchAtSubmit], a.[MeritAtSubmit], a.[EvaluationJson], a.[Commitment], a.[HoursPerWeek], a.[PortfolioCount],
           a.[Advantages] AS [AdvantagesJson], a.[GithubUsername]
    FROM [Applications] a
    JOIN [Users] f ON f.[Id] = a.[FreelancerId]
    JOIN [Opportunities] c ON c.[Id] = a.[OpportunityId]
    JOIN [Users] k ON k.[Id] = c.[ClientId]
    WHERE @scope = 0
       OR (@scope = 1 AND c.[ClientId] = @me)
       OR (@scope = 2 AND a.[FreelancerId] = @me)
    ORDER BY a.[SubmittedAtUtc] DESC, a.[Id] DESC
    OPTION (RECOMPILE);
END
