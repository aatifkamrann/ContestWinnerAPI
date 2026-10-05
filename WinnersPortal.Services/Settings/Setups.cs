using WinnersPortal.Services.Email;
using WinnersPortal.Services.Phone;
using WinnersPortal.Services.Preview;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinnersPortal.Services.Settings;

/// <summary>
/// A connection the portal can hold more than one of — GitHub Apps, object
/// stores, email setups, SMS gateways, AI providers — with one of them active
/// at a time. Each kind keeps its setups as a list under <see cref="ListKey"/>,
/// and each setup's values are ordinary settings: the first setup ever made (<see cref="Setups.MainId"/>)
/// keeps the original keys, so what an install already had — and every
/// environment variable that locks it — carries on as that setup unchanged;
/// any other setup's keys carry its id (<c>github.s1a2b3c4d.appId</c>).
/// </summary>
/// <param name="Fields">The settings a single setup is made of, by their main keys.</param>
/// <param name="Later">
/// Fields added after setups of this kind were already being tested. A test's
/// fingerprint (SetupTestLog) leaves one out while it is blank or at its
/// default, so adding it did not turn every passed test into "Changed since test".
/// </param>
/// <param name="Unproved">
/// Fields a test says nothing about — a switch that decides when the setup is
/// used, not whether it works — so flipping one is not "Changed since test".
/// </param>
public sealed record SetupKind(
    string Group, string ListKey, IReadOnlyList<string> Fields, IReadOnlyList<string>? Later = null, IReadOnlyList<string>? Unproved = null);

/// <summary>One setup as the list keeps it: which, what the administrator calls it, and whether it is the active one.</summary>
public sealed record SetupEntry(string Id, string Name, bool Enabled);

/// <summary>One setup with its values resolved — environment variable, stored row, default — as the portal will use them.</summary>
public sealed record SetupValues(string Id, string Name, bool Enabled, IReadOnlyDictionary<string, string?> Values)
{
    /// <summary>A field's value by its main key (<c>github.appId</c>), whichever setup this is.</summary>
    public string? Get(string fieldKey) => Values.GetValueOrDefault(fieldKey);
}

public static partial class Setups
{
    /// <summary>The setup whose fields are the original keys. It is where an install's existing values live.</summary>
    public const string MainId = "main";
    public const string MainName = "Main";
    public const int MaxPerKind = 10;
    public const int MaxNameLength = 60;

    public static readonly SetupKind GitHub = new("github", "setups.github",
    [
        "github.appId", "github.appSlug", "github.organization", "github.privateKey",
        "github.webhookSecret", "github.clientId", "github.clientSecret", "github.transferToken",
    ]);

    public static readonly SetupKind Storage = new("storage", "setups.storage",
    [
        "storage.endpoint", "storage.publicUrl", "storage.bucket", "storage.region",
        "storage.accessKey", "storage.secretKey",
    ]);

    /// <summary>
    /// The way it sends first, then the SMTP server's fields in the order they
    /// always had, then the mail APIs'. The settings screen shows a setup only
    /// the fields its way of sending reads (Email/EmailProviders.cs).
    /// </summary>
    public static readonly SetupKind Email = new("email", "setups.email",
    [
        EmailProviders.ProviderKey,
        "email.smtpHost", "email.smtpPort", "email.fromAddress", "email.smtpUser", "email.smtpPassword",
        EmailProviders.ApiKeyKey, EmailProviders.DomainKey, EmailProviders.RegionKey,
    ],
    Later: [EmailProviders.ProviderKey, EmailProviders.ApiKeyKey, EmailProviders.DomainKey, EmailProviders.RegionKey]);

    public static readonly SetupKind Phone = new("phone", "setups.phone",
    [
        PhoneSender.ProviderKey, PhoneSender.ApiKeyKey, PhoneSender.SenderKey,
        PhoneSender.UrlKey, PhoneSender.HeaderKey, PhoneSender.BodyKey,
    ]);

    /// <summary>
    /// The provider, key and model, and whether the setup stands by for the
    /// active one (<see cref="AiOptions.StandbyKey"/>): the switches, the
    /// ceiling and the features govern every provider at once.
    /// </summary>
    public static readonly SetupKind Ai = new("ai", "setups.ai", ["ai.provider", "ai.apiKey", "ai.model", AiOptions.StandbyKey],
        Unproved: [AiOptions.StandbyKey]);

    /// <summary>
    /// The provider, its key, Didit's workflow and webhook secret, and Shufti
    /// Pro's client ID; the switches govern every setup at once. The screen
    /// shows a setup only the fields its provider reads (IdentityProviders.FieldsOf).
    /// </summary>
    public static readonly SetupKind Identity = new("identity", "setups.identity",
    [
        "identity.provider", "identity.clientId", "identity.apiKey", "identity.workflowId", "identity.webhookSecret",
    ],
    Later: ["identity.clientId"]);

    /// <summary>
    /// Where the build agent answers and the token it was installed with;
    /// the timeout is the host's own limit. The three preview fields came
    /// later: where previews live, when an unvisited one stops, and how
    /// many may run at once.
    /// </summary>
    public static readonly SetupKind Preview = new("preview", "setups.preview",
    [
        PreviewHostKeys.HostUrl, PreviewHostKeys.Token, PreviewHostKeys.BuildTimeoutMinutes,
        PreviewHostKeys.RunUrl, PreviewHostKeys.IdleMinutes, PreviewHostKeys.MaxRunning,
    ],
    Later: [PreviewHostKeys.RunUrl, PreviewHostKeys.IdleMinutes, PreviewHostKeys.MaxRunning]);

    public static readonly IReadOnlyList<SetupKind> Kinds = [GitHub, Storage, Email, Phone, Ai, Identity, Preview];

    public static SetupKind? KindOfGroup(string group) => Kinds.FirstOrDefault(k => k.Group == group);

    public static SetupKind? KindOfList(string key) => Kinds.FirstOrDefault(k => k.ListKey == key);

    public static SetupKind? KindOfField(string fieldKey) => Kinds.FirstOrDefault(k => k.Fields.Contains(fieldKey));

    public static bool IsValidId(string? id) => id == MainId || (id is not null && IdPattern().IsMatch(id));

    public static string NewId() => "s" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The key a setup keeps one field under: the main key itself for the main setup.</summary>
    public static string FieldKey(string fieldKey, string setupId)
    {
        if (setupId == MainId) return fieldKey;
        var dot = fieldKey.IndexOf('.');
        return $"{fieldKey[..dot]}.{setupId}.{fieldKey[(dot + 1)..]}";
    }

    /// <summary>
    /// <c>github.s1a2b3c4d.appId</c> → (<c>github.appId</c>, <c>s1a2b3c4d</c>); null for any key
    /// that is not a field of a setup other than the main one.
    /// </summary>
    public static (string FieldKey, string SetupId)? ParseScoped(string key)
    {
        var first = key.IndexOf('.');
        if (first <= 0) return null;
        var second = key.IndexOf('.', first + 1);
        if (second < 0) return null;
        var id = key[(first + 1)..second];
        if (id == MainId || !IdPattern().IsMatch(id)) return null;
        var fieldKey = key[..first] + key[second..];
        return KindOfField(fieldKey) is { } kind && kind.Group == key[..first] ? (fieldKey, id) : null;
    }

    /// <summary>The list before anybody has touched it: the main setup, active, as the portal always was.</summary>
    public static IReadOnlyList<SetupEntry> Initial { get; } = [new(MainId, MainName, true)];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Wire(string? Id, string? Name, bool? Enabled);

    /// <summary>
    /// The stored list, or the initial one when there is none. A value that
    /// does not parse cannot have come through <see cref="Read"/>'s checks,
    /// so it is not trusted: the portal carries on with the main setup alone.
    /// A list saved when several setups could be switched on at once keeps
    /// every setup, and the first of those switched on as the active one.
    /// </summary>
    public static IReadOnlyList<SetupEntry> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return Initial;
        var (list, problem) = ReadEntries(stored);
        if (problem is not null || list is null) return Initial;
        var active = list.FirstOrDefault(s => s.Enabled)?.Id;
        return list.Select(s => s with { Enabled = s.Id == active }).ToList();
    }

    /// <summary>
    /// A list as the settings screen sends it, checked: every setup has a
    /// known id once, a name of its own, and a state — and no more than one
    /// is active. Names are trimmed, so what is stored is what
    /// <see cref="Write"/> would write.
    /// </summary>
    public static (IReadOnlyList<SetupEntry>? List, string? Problem) Read(string json)
    {
        var (list, problem) = ReadEntries(json);
        if (problem is not null) return (null, problem);
        return list!.Count(s => s.Enabled) > 1
            ? (null, "Only one setup can be active at a time — switch the others off.")
            : (list, null);
    }

    private static (IReadOnlyList<SetupEntry>? List, string? Problem) ReadEntries(string json)
    {
        List<Wire?>? wire;
        try
        {
            wire = JsonSerializer.Deserialize<List<Wire?>>(json, Json);
        }
        catch (JsonException)
        {
            return (null, "The list of setups is not valid JSON.");
        }
        if (wire is null) return (null, "The list of setups is missing.");
        if (wire.Count > MaxPerKind) return (null, $"A connection can have at most {MaxPerKind} setups.");

        var list = new List<SetupEntry>();
        foreach (var item in wire)
        {
            if (item is null || !IsValidId(item.Id))
                return (null, "A setup has no id, or one the portal did not give it.");
            var name = item.Name?.Trim() ?? "";
            if (name.Length == 0) return (null, "Every setup needs a name.");
            if (name.Length > MaxNameLength)
                return (null, $"“{name[..20]}…” is too long for a setup name — keep it to {MaxNameLength} characters.");
            if (list.Any(s => s.Id == item.Id)) return (null, "The same setup appears twice in the list.");
            if (list.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                return (null, $"Two setups are called “{name}” — give each its own name.");
            list.Add(new SetupEntry(item.Id!, name, item.Enabled ?? false));
        }
        return (list, null);
    }

    public static string Write(IEnumerable<SetupEntry> list) =>
        JsonSerializer.Serialize(list.Select(s => new Wire(s.Id, s.Name, s.Enabled)), Json);

    /// <summary>
    /// The one setup that takes new work, or null while none is active. The
    /// others take nothing new; what already lives in one of them — a file,
    /// a repository — is still reached through it (StorageService, GitHubService).
    /// </summary>
    public static SetupValues? Active(IEnumerable<SetupValues> setups) => setups.FirstOrDefault(s => s.Enabled);

    [GeneratedRegex("^s[0-9a-f]{8}$")]
    private static partial Regex IdPattern();
}
