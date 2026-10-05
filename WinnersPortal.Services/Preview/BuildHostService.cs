using WinnersPortal.Services.Common;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Preview;

/// <summary>
/// Where the build host stands for the rest of the portal.
/// </summary>
/// <param name="Config">
/// The active setup's connection, or null when none is active or it lacks
/// an address or a token. What is already on the host — a build under way,
/// a preview running, a stop someone pressed — is followed through it
/// whether or not the host is ready for anything new.
/// </param>
/// <param name="Problem">Why builds are off, for an administrator; null when they are on.</param>
public sealed record BuildHostState(PreviewHostConfig? Config, string? Problem)
{
    /// <summary>Builds are on: the form offers Docker Compose, the board shows builds, and new work is handed over.</summary>
    public bool Ready => Problem is null;

    public const string NoneActive = "No build host is switched on (Settings → Build host).";
    public const string Incomplete = "The active build host has no address or no agent token.";
    public const string NotTested = "The active build host has not been tested — press Test build host.";
    public const string TestFailed = "The active build host failed its last test.";
    public const string ChangedSinceTest = "The active build host has changed since its last test — test it again.";

    /// <summary>
    /// Builds are on only for an active, complete setup whose last test
    /// passed and still speaks for what is saved — the card's green badge.
    /// A host nobody has shown to answer is not one to promise clients.
    /// </summary>
    public static BuildHostState Judge(SetupValues? active, SetupTestDto? last)
    {
        if (active is null) return new(null, NoneActive);
        if (PreviewHost.Config(active) is not { } config) return new(null, Incomplete);
        var problem = last switch
        {
            null => NotTested,
            { Ok: false } => TestFailed,
            { Changed: true } => ChangedSinceTest,
            _ => null,
        };
        return new(config, problem);
    }
}

/// <summary>
/// Whether the portal offers Docker Compose builds at all. Until the
/// active build host has passed its test, the opportunity form does not
/// offer the requirement and refuses it, the progress board carries no
/// builds, no final preview and no Builds line in a standing, and nothing
/// new is handed to the host; a build already queued waits, Pending, and
/// goes the moment a test passes.
/// </summary>
public sealed class BuildHostService(SettingsService settings, SetupTestLog tests)
{
    public async Task<BuildHostState> StateAsync(CancellationToken ct)
    {
        var active = await settings.ActiveSetupAsync(Setups.Preview, ct);
        var last = active is null ? null
            : (await tests.ReadAsync(Setups.Preview, [active], ct)).GetValueOrDefault(active.Id);
        return BuildHostState.Judge(active, last);
    }

    public async Task<bool> ReadyAsync(CancellationToken ct) => (await StateAsync(ct)).Ready;

    /// <summary>For the opportunity form: whether to offer the requirement at all. The reason stays on the settings screen.</summary>
    public async Task<Outcome<BuildHostStatusResponse>> StatusAsync(CancellationToken ct) =>
        Outcome.Ok(new BuildHostStatusResponse { Ready = await ReadyAsync(ct) });
}
