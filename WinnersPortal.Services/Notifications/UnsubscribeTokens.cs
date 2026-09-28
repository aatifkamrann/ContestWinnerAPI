using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace WinnersPortal.Services.Notifications;

/// <summary>
/// The one-click unsubscribe link at the foot of every opted-in email. The
/// token is the user id under data protection: it proves the link came
/// from an email the portal sent, works without signing in (the inbox is
/// the proof), never expires (an old email should still be able to stop
/// new ones), and reveals nothing when read.
/// </summary>
public sealed class UnsubscribeTokens(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("WinnersPortal.Notifications.Unsubscribe");

    public string Create(Guid userId) => _protector.Protect(userId.ToString("N"));

    /// <summary>The portal-relative path the email carries; absolutized at send time like every link.</summary>
    public string Path(Guid userId) => "/notifications/unsubscribe?token=" + Create(userId);

    /// <summary>The user the token names, or null for anything tampered, truncated or made up.</summary>
    public Guid? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            return Guid.TryParseExact(_protector.Unprotect(token.Trim()), "N", out var id) ? id : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
