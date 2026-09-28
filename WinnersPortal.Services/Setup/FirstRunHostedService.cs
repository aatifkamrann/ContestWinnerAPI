using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Services.Settings;

namespace WinnersPortal.Services.Setup;

/// <summary>
/// Runs once at boot, after migrations. If setup is incomplete it either
/// auto-completes it (SETUP_AUTO=true — the compose demo path, so the demo
/// never lands on a form) or logs the wizard URL with the one-time token.
/// Under SETUP_AUTO it also seeds demo opportunities and accounts into an empty
/// database, so the feed the demo opens on is never blank.
/// </summary>
public sealed class FirstRunHostedService(
    IServiceScopeFactory scopes,
    SettingsService settings,
    SetupToken token,
    IConfiguration config,
    ILogger<FirstRunHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var completed = await settings.IsSetupCompletedAsync(ct);

        if (config.GetValue<bool>("SETUP_AUTO"))
        {
            if (!completed)
            {
                var email = config["SETUP_ADMIN_EMAIL"];
                var password = config["SETUP_ADMIN_PASSWORD"];
                if (string.IsNullOrWhiteSpace(email)) email = "admin@winnersportal.local";
                if (string.IsNullOrWhiteSpace(password)) password = "winners-demo";
                var portalName = config["SETUP_PORTAL_NAME"] ?? "Winners Portal";

                using var scope = scopes.CreateScope();
                var setup = scope.ServiceProvider.GetRequiredService<SetupService>();
                await setup.CompleteAsync(email, password, "Administrator",
                    new Dictionary<string, string?> { ["branding.portalName"] = portalName },
                    changedBy: "auto-setup", ct);

                log.LogWarning(
                    "SETUP_AUTO bootstrapped the portal. Sign in as {Email} / {Password} — change this password.",
                    email, password);
            }

            if (config.GetValue<bool?>("SETUP_DEMO_DATA") ?? true)
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (await DemoSeed.RunAsync(db, ct))
                    log.LogWarning(
                        "Seeded demo data. Client: client@winnersportal.local, freelancer: freelancer@winnersportal.local (password: {Password}).",
                        DemoSeed.Password);
            }
            return;
        }

        if (!completed)
            log.LogWarning(
                "First run: open /setup?token={Token} to configure the portal. (Pin the token with SETUP_TOKEN.)",
                token.Value);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
