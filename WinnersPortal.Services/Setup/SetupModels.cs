namespace WinnersPortal.Services.Setup;

public sealed record SetupAdminRequest(string? Email, string? Password, string? DisplayName);

/// <summary>The wizard's database step when it chose to move: the provider word and the connection string.</summary>
public sealed record SetupDatabaseRequest(string? Provider, string? ConnectionString);

public sealed record SetupRequest(
    string? Token,
    SetupAdminRequest? Admin,
    Dictionary<string, string?>? Settings,
    SetupDatabaseRequest? Database = null);
