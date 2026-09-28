-- The operations console's three reads in one round trip: repositories
-- that failed or are still pending after an attempt, the newest awards
-- with their handovers, and the newest webhook deliveries.
CREATE OR ALTER PROCEDURE [dbo].[Admin_Operations]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (50) e.[Id], e.[GithubUsername], f.[DisplayName] AS [Freelancer],
           c.[Title] AS [OpportunityTitle], c.[Slug] AS [OpportunitySlug], c.[Status] AS [OpportunityStatus],
           e.[ProvisionStatus] AS [Status], e.[ProvisionAttempts] AS [Attempts],
           e.[ProvisionAttemptedAtUtc] AS [LastAttemptAtUtc], e.[ProvisionNote] AS [Note]
    FROM [Entries] e
    JOIN [Users] f ON f.[Id] = e.[FreelancerId]
    JOIN [Opportunities] c ON c.[Id] = e.[OpportunityId]
    WHERE e.[Status] = 0 /* Active */
      AND (e.[ProvisionStatus] = 2 /* Failed */
           OR (e.[ProvisionStatus] = 0 /* Pending */ AND e.[ProvisionAttempts] > 0))
    ORDER BY e.[ProvisionStatus] DESC, e.[CreatedAtUtc];

    SELECT TOP (25) a.[Id], c.[Title] AS [OpportunityTitle], c.[Slug] AS [OpportunitySlug],
           k.[DisplayName] AS [Client], k.[GithubLogin] AS [ClientGithubLogin],
           w.[DisplayName] AS [Winner], e.[RepoFullName], a.[Amount], a.[Currency], a.[AnnouncedAtUtc], a.[PaidAtUtc],
           a.[Handover], a.[TransferTargetLogin], a.[TransferRequestedAtUtc], a.[HandoverVerifiedAtUtc],
           a.[HandoverNote] AS [Note]
    FROM [Awards] a
    JOIN [Opportunities] c ON c.[Id] = a.[OpportunityId]
    JOIN [Users] k ON k.[Id] = c.[ClientId]
    JOIN [Entries] e ON e.[Id] = a.[EntryId]
    JOIN [Users] w ON w.[Id] = e.[FreelancerId]
    ORDER BY a.[AnnouncedAtUtc] DESC;

    SELECT TOP (50) d.[Id], d.[Source], d.[DeliveryId], d.[Event], d.[Action], d.[RepoFullName], d.[HandledNote], d.[ReceivedAtUtc]
    FROM [WebhookDeliveries] d
    ORDER BY d.[ReceivedAtUtc] DESC;
END
