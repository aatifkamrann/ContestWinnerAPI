-- The card's own numbers, recomputed from the source of truth in one
-- statement — never incremented, so concurrent writes cannot drift a
-- counter and a write missed by a crash heals on the next one.
CREATE OR ALTER PROCEDURE [dbo].[Recount_Opportunity]
    @opportunityId uniqueidentifier
AS
BEGIN
    UPDATE c SET
        [ActiveEntryCount] = (SELECT count(*) FROM [Entries] e
            WHERE e.[OpportunityId] = c.[Id] AND e.[Status] = 0),
        [MilestoneCount] = (SELECT count(*) FROM [Milestones] m
            WHERE m.[OpportunityId] = c.[Id])
    FROM [Opportunities] c
    WHERE c.[Id] = @opportunityId;
END
