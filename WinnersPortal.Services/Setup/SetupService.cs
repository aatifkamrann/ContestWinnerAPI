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
            var def = SettingsRegistry.Find(key)
                ?? throw new SettingsValidationException($"Unknown setting '{key}'.");
            if (SettingsService.ValueProblem(def, value) is { } problem)
                throw new SettingsValidationException(problem);
        }
        // The two addresses together, as the wizard leaves them: an omitted
        // or blank one is read as it stands.
        var given = initialSettings ?? new Dictionary<string, string?>();
        string? Or(string? value, string? standing) => string.IsNullOrEmpty(value) ? standing : value;
        if (WebOrigin.PairProblem(
                Or(given.GetValueOrDefault(WebOrigin.WebUrlKey), await settings.GetAsync(WebOrigin.WebUrlKey, ct)),
                Or(given.GetValueOrDefault(WebOrigin.ApiUrlKey), await settings.GetAsync(WebOrigin.ApiUrlKey, ct))) is { } pair)
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

        // The wizard shows what the deployment's environment sets and sends
        // it back. A value it only echoes is left to the environment, where
        // it is in force while nothing is saved: saving it would freeze
        // today's variable over tomorrow's. What the person changed is saved,
        // and wins.
        var updates = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in given)
        {
            if (EchoesEnvironment(SettingsService.EnvOf(key), value, await settings.SourceAsync(key, ct))) continue;
            updates[key] = value;
        }
        updates["system.setupCompleted"] = "true";
        await settings.SetManyAsync(updates, changedBy, allowSystem: true, ct: ct);

        log.LogInformation("Setup completed; administrator {Email} created.", admin.Email);
        return admin;
    }

    /// <summary>
    /// Whether the wizard only sends back what the deployment's environment
    /// supplies: the same value, with nothing saved over it. Such a value is
    /// left to the environment rather than saved.
    /// </summary>
    public static bool EchoesEnvironment(string? env, string? value, SettingSource source) =>
        env is not null && string.Equals(env, value, StringComparison.Ordinal) && source == SettingSource.Environment;
}
