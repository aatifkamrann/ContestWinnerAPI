namespace WinnersPortal.Domain;

/// <summary>
/// One stored configuration value. Secrets are encrypted with ASP.NET Data
/// Protection before they land in <see cref="Value"/>; environment variables
/// override stored values entirely (see SettingsService).
/// </summary>
public sealed class Setting
{
    public required string Key { get; set; }

    /// <summary>Plaintext for ordinary settings, ciphertext when <see cref="IsSecret"/>.</summary>
    public string? Value { get; set; }

    public bool IsSecret { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}
