-- The entries report's rows: every entry with its entrant, opportunity and
-- client, newest first, and every claim — two result sets. @scope is 0
-- for every entry (an administrator), 1 for those on opportunities @me
-- posted, 2 for @me's own, the deselected left out.
CREATE OR ALTER PROCEDURE [dbo].[Report_Entries]
    @scope int,
    @me uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    SELECT e.[Id], e.[Status], e.[FreelancerId], f.[DisplayName] AS [FreelancerName], f.[Email] AS [FreelancerEmail],
           e.[OpportunityId], c.[Slug] AS [OpportunitySlug], c.[Title] AS [OpportunityTitle], c.[Status] AS [OpportunityStatus],
           c.[ClientId], k.[DisplayName] AS [ClientName], c.[Category], c.[Subcategory], c.[Delivery], c.[AwardAmount],
           c.[Currency], c.[DeadlineUtc], c.[CancelledAtUtc], e.[Note], e.[GithubUsername], e.[RepoFullName],
           e.[ProvisionStatus], e.[PushCount], e.[LastPushAtUtc], e.[TreeFileCount], e.[TreeHasTests], e.[TreeHasReadme],
           e.[TreeHasCi], e.[TreeHasCompose], e.[CreatedAtUtc], e.[WithdrawnAtUtc], e.[WithdrawnReason], e.[RemovedAtUtc], e.[RemovedReason],
           (SELECT COUNT(*) FROM [Submissions] s WHERE s.[EntryId] = e.[Id] AND s.[UploadedAtUtc] IS NOT NULL) AS [FilesUploaded]
    FROM [Entries] e
    JOIN [Users] f ON f.[Id] = e.[FreelancerId]
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    JOIN [Users] k ON k.[Id] = c.[ClientId]
    WHERE @scope = 0
       OR (@scope = 1 AND c.[ClientId] = @me)
       OR (@scope = 2 AND e.[FreelancerId] = @me AND e.[Status] <> 3 /* Deselected */)
    ORDER BY e.[CreatedAtUtc] DESC, e.[Id] DESC
    OPTION (RECOMPILE);

    SELECT cp.[EntryId], m.[Order], cp.[ClaimedAtUtc]
    FROM [Checkpoints] cp
    JOIN [Milestones] m ON m.[Id] = cp.[MilestoneId]
    JOIN [Entries] e ON e.[Id] = cp.[EntryId]
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    WHERE @scope = 0
       OR (@scope = 1 AND c.[ClientId] = @me)
       OR (@scope = 2 AND e.[FreelancerId] = @me AND e.[Status] <> 3 /* Deselected */)
    OPTION (RECOMPILE);
END
