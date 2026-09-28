namespace WinnersPortal.Services.Profiles;

/// <summary>
/// One thing a method has to know before money can move. The label is what
/// the member reads above the box, the placeholder is the example under it,
/// and <see cref="Required"/> is the difference between a detail a client
/// can work without and one that makes the whole row useless.
/// </summary>
public sealed record PaymentField(
    string Key,
    string Label,
    string Placeholder,
    bool Required = true,
    int MaxLength = 120);

/// <summary>
/// A way of being paid, and the fields it needs. Picking the method is what
/// decides which boxes appear — a bank transfer and a crypto address have
/// nothing in common but the money.
/// </summary>
/// <param name="Identity">
/// The field that says <em>which account</em> this is. Two rows of the same
/// method with the same value here are the same account written twice, which
/// is the duplicate the profile refuses.
/// </param>
public sealed record PaymentMethod(
    string Key,
    string Label,
    string Hint,
    string Identity,
    IReadOnlyList<PaymentField> Fields);

/// <summary>
/// Every way this portal knows how to describe being paid. The portal moves
/// no money itself — a client pays the winner directly and then marks the
/// award paid — so nothing here is an integration. It is the set of
/// questions the portal knows to ask, so that "how do I pay you" is answered
/// once on a profile instead of over email every time, and answered
/// completely: a bank row with no account number helps nobody.
///
/// Adding a method is a new entry here and nothing else. This registry
/// drives the form, the validation, and the labels a client reads.
/// </summary>
public static class PaymentMethods
{
    public const int MaxFieldValue = 300;

    public static readonly IReadOnlyList<PaymentMethod> All =
    [
        new("bank", "Bank transfer",
            "The usual answer inside one country, and the slow, cheap answer between two.",
            Identity: "number",
            [
                new("holder", "Account holder", "Exactly as the bank has it"),
                new("bank", "Bank", "e.g. Habib Bank Limited"),
                new("number", "Account number or IBAN", "e.g. PK36SCBL0000001123456702", MaxLength: 60),
                new("swift", "SWIFT / BIC", "Only needed for a payment from abroad", Required: false, MaxLength: 24),
                new("branch", "Branch or city", "e.g. Gulberg, Lahore", Required: false),
            ]),

        new("wallet", "Mobile wallet",
            "JazzCash, Easypaisa, SadaPay, NayaPay — paid to a phone number.",
            Identity: "number",
            [
                new("provider", "Wallet", "e.g. JazzCash, Easypaisa, SadaPay", MaxLength: 60),
                new("holder", "Account holder", "The name the wallet is registered to"),
                new("number", "Mobile number or wallet ID", "e.g. +92 300 1234567", MaxLength: 40),
            ]),

        new("paypal", "PayPal",
            "Quick across borders, and the fees come out of what arrives.",
            Identity: "account",
            [
                new("account", "PayPal email or PayPal.me link", "e.g. you@example.com", MaxLength: 160),
            ]),

        new("wise", "Wise",
            "Mid-market rate and a visible fee — usually the cheapest way to cross a currency.",
            Identity: "account",
            [
                new("account", "Email on the Wise account", "e.g. you@example.com", MaxLength: 160),
                new("currency", "Currency to receive", "e.g. USD, EUR, GBP", Required: false, MaxLength: 12),
            ]),

        new("payoneer", "Payoneer",
            "Common where a marketplace already pays into it.",
            Identity: "account",
            [
                new("account", "Email on the Payoneer account", "e.g. you@example.com", MaxLength: 160),
            ]),

        new("crypto", "Crypto",
            "Say the network as well as the coin. An address is worthless on the wrong chain.",
            Identity: "address",
            [
                new("coin", "Coin and network", "e.g. USDT (TRC-20), BTC, ETH (ERC-20)", MaxLength: 60),
                new("address", "Wallet address", "The address a client pastes into their wallet", MaxLength: 160),
                new("memo", "Memo or tag", "Only where the exchange asks for one", Required: false, MaxLength: 60),
            ]),

        new("remittance", "Western Union / MoneyGram",
            "Cash over a counter. The name has to match the identity document exactly.",
            Identity: "name",
            [
                new("service", "Service", "e.g. Western Union, MoneyGram, Ria", MaxLength: 60),
                new("name", "Full legal name", "As printed on the ID you will show"),
                new("place", "City and country", "Where you will collect it"),
                new("phone", "Mobile number", "So they can send you the reference", Required: false, MaxLength: 40),
            ]),

        new("other", "Something else",
            "Anything the list above does not cover — say what the client has to do.",
            Identity: "how",
            [
                new("how", "How to pay you", "Name the service and what you need from them", MaxLength: MaxFieldValue),
            ]),
    ];

    public static PaymentMethod? Find(string? key)
    {
        var trimmed = key?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return All.FirstOrDefault(m => string.Equals(m.Key, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The fields a method asked for, cleaned and in the registry's order.
    /// Anything the form sent that this method never asked about is dropped
    /// rather than stored: a field nobody will ever read is a field nobody
    /// checked the sensitivity of.
    /// </summary>
    public static Dictionary<string, string> CleanDetails(
        PaymentMethod method, IReadOnlyDictionary<string, string?>? sent)
    {
        var cleaned = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sent is null) return cleaned;
        foreach (var field in method.Fields)
        {
            sent.TryGetValue(field.Key, out var raw);
            var value = ProfileRules.Clean(raw, Math.Min(field.MaxLength, MaxFieldValue));
            if (value is not null) cleaned[field.Key] = value;
        }
        return cleaned;
    }

    /// <summary>
    /// Two rows describe the same account when they are the same method and
    /// their identifying field matches once spacing and case are set aside —
    /// an IBAN written "PK36 SCBL …" once and "pk36scbl…" the next time is
    /// one account, and a client sent both would rightly ask which is real.
    /// </summary>
    public static string Identity(PaymentMethod method, IReadOnlyDictionary<string, string> details)
    {
        details.TryGetValue(method.Identity, out var value);
        var stripped = new string((value ?? string.Empty)
            .Where(c => !char.IsWhiteSpace(c) && Separators.IndexOf(c) < 0)
            .ToArray());
        return method.Key + ":" + stripped.ToLowerInvariant();
    }

    /// <summary>Punctuation people put in account numbers to make them readable.</summary>
    private const string Separators = "-./";

    /// <summary>
    /// What the payment list gets refused for, or null if it is fine. Runs
    /// on cleaned rows, so a blank required field here means the member
    /// really left it blank rather than typed spaces into it.
    /// </summary>
    public static string? Problem(IReadOnlyList<(PaymentMethod Method, Dictionary<string, string> Details)> rows)
    {
        if (rows.Count > ProfileRules.MaxPayments)
            return $"At most {ProfileRules.MaxPayments} ways to be paid.";

        foreach (var (method, details) in rows)
        {
            var missing = method.Fields.FirstOrDefault(f => f.Required && !details.ContainsKey(f.Key));
            if (missing is not null)
                return $"{method.Label} needs {missing.Label.ToLowerInvariant()} — a client cannot pay without it.";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (method, details) in rows)
            if (!seen.Add(Identity(method, details)))
                return $"The same {method.Label.ToLowerInvariant()} account is listed twice — one row each.";

        return null;
    }
}
