using Microsoft.AspNetCore.Identity;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Setup;

/// <summary>Completes first-run setup: creates the first administrator and writes the initial settings.</summary>
public sealed class SetupService(AppDbContext db, SettingsService settings, ILogger<SetupService> log)
{
    private static readonly PasswordHasher<User> Hasher = new();

    public async Task<User> CompleteAsync(
        string email,
        string password,
        string displayName,
        IReadOnlyDictionary<string, string?>? initialSettings,
        string changedBy,
        CancellationToken ct = default)
    {
        if (await settings.IsSetupCompletedAsync(ct))
            throw new InvalidOperationException("Setup has already been completed.");
        // Refused before the administrator exists: a value turned down after
        // would leave an account behind and the wizard unable to finish.
        foreach (var (key, value) in initialSettings ?? new Dictionary<string, string?>())
        {
            if (SettingsService.IsLocked(key)) continue;
            var def = SettingsRegistry.Find(key)
                ?? throw new SettingsValidationException($"Unknown setting '{key}'.");
            if (SettingsService.ValueProblem(def, value) is { } problem)
                throw new SettingsValidationException(problem);
        }
        // The two addresses together, as the wizard leaves them: a locked
        // or omitted one is read as it stands.
        var given = initialSettings ?? new Dictionary<string, string?>();
        if (WebOrigin.PairProblem(
                given.TryGetValue(WebOrigin.WebUrlKey, out var webUrl) && !SettingsService.IsLocked(WebOrigin.WebUrlKey)
                    ? webUrl : await settings.GetAsync(WebOrigin.WebUrlKey, ct),
                given.TryGetValue(WebOrigin.ApiUrlKey, out var apiUrl) && !SettingsService.IsLocked(WebOrigin.ApiUrlKey)
                    ? apiUrl : await settings.GetAsync(WebOrigin.ApiUrlKey, ct)) is { } pair)
            throw new SettingsValidationException(pair);

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = email.Trim().ToLowerInvariant(),
            DisplayName = displayName.Trim(),
            PasswordHash = "",
            Role = Roles.Admin,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            // The person running the wizard is at the keyboard: no code.
            EmailConfirmedAtUtc = DateTimeOffset.UtcNow,
        };
        admin.PasswordHash = Hasher.HashPassword(admin, password);
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);

        var updates = new Dictionary<string, string?>(
            initialSettings ?? new Dictionary<string, string?>(), StringComparer.Ordinal)
        {
            ["system.setupCompleted"] = "true",
        };
        // The wizard may echo back env-locked keys; skip those rather than fail setup.
        await settings.SetManyAsync(updates, changedBy, allowSystem: true, skipLocked: true, ct: ct);

        log.LogInformation("Setup completed; administrator {Email} created.", admin.Email);
        return admin;
    }
}
