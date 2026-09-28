namespace WinnersPortal.Services.GitHub;

/// <summary>What the portal made of a webhook delivery.</summary>
public sealed record WebhookResponse
{
    public required bool Ok { get; init; }
    public required string Note { get; init; }
}
