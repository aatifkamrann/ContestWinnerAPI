-- A freelancer's part in each opportunity: entries oldest first, applications,
-- and the opportunities they won — three result sets over the opportunity ids as
-- JSON.
CREATE OR ALTER PROCEDURE [dbo].[Report_MyParts]
    @ids nvarchar(max),
    @me uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @opportunities TABLE ([Id] uniqueidentifier PRIMARY KEY);
    INSERT INTO @opportunities SELECT [Id] FROM OPENJSON(@ids) WITH ([Id] uniqueidentifier '$');

    SELECT e.[OpportunityId], e.[Status]
    FROM [Entries] e
    JOIN @opportunities ids ON ids.[Id] = e.[OpportunityId]
    WHERE e.[FreelancerId] = @me
    ORDER BY e.[CreatedAtUtc];

    SELECT a.[OpportunityId], a.[Status]
    FROM [Applications] a
    JOIN @opportunities ids ON ids.[Id] = a.[OpportunityId]
    WHERE a.[FreelancerId] = @me;

    SELECT a.[OpportunityId]
    FROM [Awards] a
    JOIN @opportunities ids ON ids.[Id] = a.[OpportunityId]
    JOIN [Entries] e ON e.[Id] = a.[EntryId]
    WHERE e.[FreelancerId] = @me;
END
