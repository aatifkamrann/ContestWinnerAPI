namespace WinnersPortal.Services.Preview;

/// <summary>Whether Docker Compose builds are on: a build host is switched on and has passed its test.</summary>
public sealed record BuildHostStatusResponse
{
    public required bool Ready { get; init; }
}
