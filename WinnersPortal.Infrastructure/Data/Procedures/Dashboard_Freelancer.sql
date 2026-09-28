-- A freelancer's dashboard: four result sets over their newest two hundred
-- entries.
CREATE OR ALTER PROCEDURE [dbo].[Dashboard_Freelancer]
    @userId uniqueidentifier,
    @since datetimeoffset
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (200) e.[Id], e.[OpportunityId], e.[Status], e.[ProvisionStatus], e.[RepoFullName], e.[LastPushAtUtc], e.[PushCount],
           e.[CreatedAtUtc],
           (SELECT COUNT(*) FROM [Checkpoints] cp WHERE cp.[EntryId] = e.[Id]) AS [Claimed],
           c.[Slug], c.[Title], c.[Status] AS [OpportunityStatus], c.[DeadlineUtc], c.[AwardAmount], c.[Currency],
           (SELECT COUNT(*) FROM [Milestones] m WHERE m.[OpportunityId] = c.[Id]) AS [Milestones],
           c.[Delivery]
    FROM [Entries] e
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    WHERE e.[FreelancerId] = @userId
    ORDER BY e.[CreatedAtUtc] DESC;

    SELECT cp.[ClaimedAtUtc]
    FROM [Checkpoints] cp
    JOIN [Entries] e ON e.[Id] = cp.[EntryId]
    WHERE e.[FreelancerId] = @userId AND cp.[ClaimedAtUtc] >= @since;

    SELECT a.[Amount], a.[Currency], a.[AnnouncedAtUtc], a.[PaidAtUtc], a.[Handover], a.[TransferTargetLogin], a.[EntryId],
           c.[Slug], c.[Title]
    FROM [Awards] a
    JOIN [Entries] e ON e.[Id] = a.[EntryId]
    JOIN [Opportunities] c ON c.[Id] = a.[OpportunityId]
    WHERE e.[FreelancerId] = @userId;

    SELECT TOP (8) cp.[ClaimedAtUtc], cp.[Via], cp.[Ref], m.[Title] AS [Milestone], m.[Order], c.[Slug]
    FROM [Checkpoints] cp
    JOIN [Entries] e ON e.[Id] = cp.[EntryId]
    JOIN [Milestones] m ON m.[Id] = cp.[MilestoneId]
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    WHERE e.[FreelancerId] = @userId
    ORDER BY cp.[ClaimedAtUtc] DESC;
END
