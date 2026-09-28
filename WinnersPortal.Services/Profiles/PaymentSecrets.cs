using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace WinnersPortal.Services.Profiles;

/// <summary>
/// The lock on a member's payment details. Account numbers and wallet
/// addresses are the one thing on a profile that is worth stealing, so they
/// are encrypted at rest with the same data-protection key ring that guards
/// secret settings — a database dump, a stray backup or a read-only report
/// query gets ciphertext.
///
/// It is not a defence against the running application: anything the API can
/// show a client, an attacker inside the API could too. What it buys is that
/// the copy of the data most likely to leak is the copy that cannot be read.
/// </summary>
public sealed class PaymentSecrets(IDataProtectionProvider provider, ILogger<PaymentSecrets> log)
{
    private readonly IDataProtector _protector =
        provider.CreateProtector("WinnersPortal.Profiles.Payments");

    public string Protect(IReadOnlyDictionary<string, string> details) =>
        _protector.Protect(JsonSerializer.Serialize(details));

    /// <summary>
    /// The fields back, or null when the row cannot be read — a key ring
    /// that was replaced rather than carried over. Null is not an error: the
    /// form shows that row as one to type again, which is the only thing
    /// anybody could do about it, and the rest of the profile still loads.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Unprotect(string stored)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(stored));
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Could not decrypt a payment method — the member will be asked to enter it again. "
                + "Did the data-protection keys change?");
            return null;
        }
    }
}
