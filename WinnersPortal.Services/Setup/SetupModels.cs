using WinnersPortal.Services.Database;

namespace WinnersPortal.Services.Setup;

public sealed record SetupAdminRequest(string? Email, string? Password, string? DisplayName);

public sealed record SetupRequest(
    string? Token,
    SetupAdminRequest? Admin,
    Dictionary<string, string?>? Settings,
    // The wizard's database step when it chose to move; null keeps the database.
    DatabaseConnectionFields? Database = null);
