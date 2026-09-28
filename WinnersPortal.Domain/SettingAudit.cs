namespace WinnersPortal.Domain;

/// <summary>
/// Every settings change is audited. Secret values are recorded as a mask,
/// never as the value itself — the audit trail must not leak what the
/// encryption protects.
/// </summary>
public sealed class SettingAudit
{
    public long Id { get; set; }
    public required string Key { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public required string ChangedBy { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }
}
