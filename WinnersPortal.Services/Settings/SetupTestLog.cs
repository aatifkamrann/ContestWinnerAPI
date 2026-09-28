using WinnersPortal.Services.Auth;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WinnersPortal.Infrastructure.Data;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// The last test of each setup, kept so the badge on its card on the
/// settings screen outlives the click and reads the same for every
/// administrator: connected, failed, not tested — or changed since the test,
/// once a value the test ran with is no longer the one saved (or the one the
/// deployment sets). A test that never reached the connection — the AI call
/// ceiling, a number that is not a number — records nothing, because it says
/// nothing about the setup.
/// </summary>
public sealed class SetupTestLog(AppDbContext db, SettingsService settings, IDataProtectionProvider dataProtection)
{
    public const int MaxDetail = SetupTest.MaxDetail;

    private readonly IDataProtector _protector = dataProtection.CreateProtector("WinnersPortal.SetupTests");

    /// <summary>
    /// Keeps a test's answer as the setup's last, against the values it ran
    /// with — read by the caller before the test started, so a save landing
    /// mid-test is never vouched for. Null when there was no such setup.
    /// On SQL Server the rewrite is <c>SetupTest_Record</c>; nothing
    /// rewritten means the caller inserts.
    /// </summary>
    public async Task<SetupTestDto?> RecordAsync(
        SetupKind kind, SetupValues? tested, bool ok, string detail, string? by, CancellationToken ct)
    {
        if (tested is null) return null;
        var now = DateTimeOffset.UtcNow;
        var kept = Clip(detail);
        var fingerprint = _protector.Protect(Fingerprint(kind, tested));

        var updated = db.UseDapper
            ? await db.Sql.ExecuteAsync(Procedures.SetupTestRecord,
                new { kind = kind.Group, setupId = tested.Id, ok, detail = kept, fingerprint, testedAt = now, testedBy = by }, ct)
            : await db.SetupTests
                .Where(t => t.Kind == kind.Group && t.SetupId == tested.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Ok, ok)
                    .SetProperty(t => t.Detail, kept)
                    .SetProperty(t => t.Fingerprint, fingerprint)
                    .SetProperty(t => t.TestedAtUtc, now)
                    .SetProperty(t => t.TestedBy, by), ct);
        if (updated == 0)
        {
            db.SetupTests.Add(new SetupTest
            {
                Kind = kind.Group,
                SetupId = tested.Id,
                Ok = ok,
                Detail = kept,
                Fingerprint = fingerprint,
                TestedAtUtc = now,
                TestedBy = by,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another administrator's first test of the same setup landed
                // a moment earlier. Theirs is as recent as this one; it stands.
            }
        }
        return new SetupTestDto(ok, kept, now, by, Changed: false);
    }

    /// <summary>Each listed setup's last test, and whether its values have changed since.</summary>
    public async Task<Dictionary<string, SetupTestDto>> ReadAsync(
        SetupKind kind, IReadOnlyList<SetupValues> setups, CancellationToken ct)
    {
        var rows = await db.SetupTests.AsNoTracking().Where(t => t.Kind == kind.Group).ToListAsync(ct);
        var result = new Dictionary<string, SetupTestDto>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (setups.FirstOrDefault(s => s.Id == row.SetupId) is not { } setup) continue;
            result[row.SetupId] = new SetupTestDto(
                row.Ok, row.Detail, row.TestedAtUtc, row.TestedBy, Changed(_protector, row.Fingerprint, kind, setup));
        }
        return result;
    }

    /// <summary>
    /// Forgets the tests of setups no longer listed, so a setup that comes
    /// back — the main one, put back by an environment variable — starts
    /// untested rather than wearing an old answer. On SQL Server,
    /// <c>SetupTest_Prune</c> with the listed ids as JSON.
    /// </summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        foreach (var kind in Setups.Kinds)
        {
            var ids = (await settings.ListAsync(kind, ct)).Select(s => s.Id).ToList();
            if (db.UseDapper)
                await db.Sql.ExecuteAsync(Procedures.SetupTestPrune,
                    new { kind = kind.Group, ids = System.Text.Json.JsonSerializer.Serialize(ids) }, ct);
            else
                await db.SetupTests
                    .Where(t => t.Kind == kind.Group && !ids.Contains(t.SetupId))
                    .ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// A SHA-256 over a setup's fields in their fixed order, each value
    /// length-prefixed so no two sets of values run together into the same
    /// text. Its name and whether it is active are not in it: neither changes
    /// what the test proved. Unset and empty are the same value — and a field
    /// the kind gained later is not in it at all while it is blank or at its
    /// default, which is the value every setup tested before it had.
    /// </summary>
    public static string Fingerprint(SetupKind kind, SetupValues setup)
    {
        var text = new StringBuilder();
        foreach (var field in kind.Fields)
        {
            var value = setup.Get(field) ?? "";
            if (kind.Later?.Contains(field) == true
                && (value.Length == 0 || value == SettingsRegistry.Find(field)?.Default)) continue;
            text.Append(field).Append('=').Append(value.Length).Append(':').Append(value).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Whether the values a test ran with differ from a setup's values now.
    /// A fingerprint that no longer decrypts — the key ring was replaced —
    /// can vouch for nothing, so it counts as changed.
    /// </summary>
    public static bool Changed(IDataProtector protector, string stored, SetupKind kind, SetupValues now)
    {
        try
        {
            return !string.Equals(protector.Unprotect(stored), Fingerprint(kind, now), StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return true;
        }
    }

    /// <summary>
    /// A text test's answer as it is kept: the gateway may quote the number
    /// back, and the number is whoever pressed the button's own, so every
    /// other administrator sees it masked.
    /// </summary>
    public static string WithoutNumber(string detail, string number)
    {
        var masked = Auth.Confirmation.MaskPhone(number);
        return detail.Replace(number, masked, StringComparison.Ordinal)
            .Replace(number.TrimStart('+'), masked, StringComparison.Ordinal);
    }

    private static string Clip(string detail) =>
        detail.Length <= MaxDetail ? detail : detail[..(MaxDetail - 1)] + "…";
}

/// <param name="Changed">A value of the setup has changed since this test, so its answer no longer speaks for what is saved.</param>
public sealed record SetupTestDto(bool Ok, string Detail, DateTimeOffset TestedAt, string? TestedBy, bool Changed);
