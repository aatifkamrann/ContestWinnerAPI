using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// The words the API uses for an opportunity's states on the wire. One place, so
/// a card, a report, a digest and the opportunity page all say "reviewing" the
/// same way.
/// </summary>
public static class OpportunityNames
{
    public static string ProvisionName(RepoProvisionStatus s) => s switch
    {
        RepoProvisionStatus.Pending => "pending",
        RepoProvisionStatus.Provisioned => "provisioned",
        _ => "failed",
    };

    public static string StatusName(OpportunityStatus s) => s switch
    {
        OpportunityStatus.Draft => "draft",
        OpportunityStatus.Open => "open",
        OpportunityStatus.Reviewing => "reviewing",
        OpportunityStatus.Awarded => "awarded",
        _ => "cancelled",
    };
}

public static class EntryNames
{
    public static string StatusName(EntryStatus s) => s switch
    {
        EntryStatus.Active => "active",
        EntryStatus.Withdrawn => "withdrawn",
        EntryStatus.Deselected => "deselected",
        _ => "removed",
    };
}

public static class AwardNames
{
    public static string HandoverName(HandoverStatus s) => s switch
    {
        HandoverStatus.NotStarted => "not_started",
        HandoverStatus.Requested => "requested",
        _ => "verified",
    };
}
