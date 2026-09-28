using System.Security.Cryptography;
using System.Text;
using WinnersPortal.Services.Common;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Auth;

/// <summary>
/// Proof that a new account belongs to a person who can be reached. A code
/// goes to the email address and, where the portal can text, another to the
/// phone; whichever one comes back confirms the account, and until one does
/// the account can do nothing — the gate below refuses every call under
/// /api except the session's own, reads included. Registration is the only
/// door that owes this: an account an administrator made, the setup wizard's
/// own, and an invitation link used are all vouched for another way.
///
/// The two codes are different on purpose, so the row records which contact
/// was actually proved. A shared code would confirm "somebody", and the
/// phone column would then say nothing a client could lean on.
///
/// Codes are six digits, because they are typed off a phone screen; that is
/// short enough to guess, so the guard is the attempt cap and the lifetime,
/// not the hash. The hash is still kept rather than the code, so a backup or
/// an administrator's screen never holds a code somebody could still use.
/// </summary>
public static class Confirmation
{
    public const int CodeLength = 6;

    /// <summary>How long a code works. A slow SMS hop fits; a forgotten tab does not.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The floor between two sends to the same account. The account is its
    /// own, so this is not about somebody else's inbox — it is what stops a
    /// stuck retry from spending the operator's SMS allowance in a minute.
    /// </summary>
    public static readonly TimeSpan ResendInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Wrong guesses before the codes are thrown away. Six digits is a
    /// million codes; five tries against that is nothing, and a fresh pair
    /// is one click away for the person who merely mistyped.
    /// </summary>
    public const int MaxAttempts = 5;

    public static string NewCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    /// <summary>Whether either contact has been proved — the account may then do everything.</summary>
    public static bool IsConfirmed(User user) =>
        user.EmailConfirmedAtUtc is not null || user.PhoneConfirmedAtUtc is not null;

    public static DateTimeOffset? ExpiresUtc(DateTimeOffset? issuedAtUtc) => issuedAtUtc + Lifetime;

    public static bool TooSoonToResend(DateTimeOffset? issuedAtUtc, DateTimeOffset nowUtc) =>
        issuedAtUtc is { } issued && nowUtc - issued < ResendInterval;

    /// <summary>Seconds until a resend is allowed; zero when it is.</summary>
    public static int RetryAfterSeconds(DateTimeOffset? issuedAtUtc, DateTimeOffset nowUtc) =>
        issuedAtUtc is { } issued
            ? Math.Max(0, (int)Math.Ceiling((issued + ResendInterval - nowUtc).TotalSeconds))
            : 0;

    public enum Match
    {
        /// <summary>Neither code, or no code standing at all.</summary>
        None,
        Expired,
        /// <summary>The attempt cap was already spent before this guess.</summary>
        Exhausted,
        Email,
        Phone,
    }

    /// <summary>
    /// Pure so the rule is testable: what a presented code amounts to,
    /// against the hashes standing on the row. The caller counts the
    /// attempt; this only reads the count, so an exhausted row is refused
    /// before the compare rather than after.
    /// </summary>
    public static Match Verify(
        string? emailCodeHash, string? phoneCodeHash, DateTimeOffset? issuedAtUtc, int attempts,
        string? presented, DateTimeOffset nowUtc)
    {
        var code = Digits(presented);
        if (code.Length != CodeLength) return Match.None;
        if (emailCodeHash is null && phoneCodeHash is null) return Match.None;
        if (ExpiresUtc(issuedAtUtc) is not { } expires || expires < nowUtc) return Match.Expired;
        if (attempts >= MaxAttempts) return Match.Exhausted;

        var hash = Hash(code);
        if (emailCodeHash is not null && FixedEquals(hash, emailCodeHash)) return Match.Email;
        if (phoneCodeHash is not null && FixedEquals(hash, phoneCodeHash)) return Match.Phone;
        return Match.None;
    }

    /// <summary>The digits of what was typed — "123 456" and "123-456" are the code they mean.</summary>
    public static string Digits(string? presented) =>
        new((presented ?? "").Where(char.IsAsciiDigit).ToArray());

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    // ---------------------------------------------------------------- phone

    public const int MaxPhoneLength = User.MaxPhoneLength; // "+" and up to fifteen digits, per E.164

    public const string PhoneProblem =
        "Enter the phone number in international form, with the country code first — like +92 300 1234567.";

    /// <summary>
    /// A phone number as it will be stored: international form, digits only
    /// behind a leading plus, so the same number typed three ways is one
    /// number. Spaces, dashes, dots and brackets are punctuation people
    /// type and are dropped; "00" is the dialling prefix for "+" and is
    /// read as one. Anything without a country code is refused rather than
    /// guessed — a national number is a different phone in every country,
    /// and a code texted to the wrong one confirms nobody.
    /// Null (no problem) for nothing entered: the phone is optional.
    /// </summary>
    public static (string? Phone, string? Problem) CleanPhone(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return (null, null);
        if (s.StartsWith("00")) s = "+" + s[2..];
        if (!s.StartsWith('+')) return (null, PhoneProblem);
        var body = s[1..];
        if (body.Any(c => !(char.IsAsciiDigit(c) || c is ' ' or '-' or '.' or '(' or ')')))
            return (null, PhoneProblem);
        var digits = new string(body.Where(char.IsAsciiDigit).ToArray());
        // Seven is the shortest national number anywhere plus a one-digit
        // country code; fifteen is E.164's ceiling.
        if (digits.Length is < 8 or > 15 || digits[0] == '0') return (null, PhoneProblem);
        return ("+" + digits, null);
    }

    /// <summary>"b•••@example.com" — enough to recognise, not enough to copy.</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return email;
        return email[0] + "•••" + email[at..];
    }

    /// <summary>"+92 ••• 4567" — the country code and the last four.</summary>
    public static string MaskPhone(string phone)
    {
        var digits = phone.TrimStart('+');
        if (digits.Length <= 4) return phone;
        var cc = digits[..Math.Min(2, digits.Length - 4)];
        return $"+{cc} ••• {digits[^4..]}";
    }

    /// <summary>The whole SMS. Short, because the operator may be paying per segment.</summary>
    public static string Text(string portalName, string code) =>
        $"{code} is your {portalName} code. It works for {(int)Lifetime.TotalMinutes} minutes.";

    // ----------------------------------------------------------------- gate

    /// <summary>
    /// Pure so the rule is testable: may this request proceed while the
    /// account is unconfirmed? Only the session's own endpoints — the code
    /// itself, a fresh one, who am I, signing out — and what any stranger
    /// may read anyway. Everything else waits, reads included: an
    /// unconfirmed account is not yet a member, and a member is what the
    /// opportunity pages are for.
    /// </summary>
    public static bool GateAllows(string path)
    {
        if (!Requests.StartsWithSegments(path, "/api")) return true;
        return Requests.StartsWithSegments(path, "/api/auth")
            || Requests.StartsWithSegments(path, "/api/public")
            || Requests.StartsWithSegments(path, "/api/help")
            || Requests.StartsWithSegments(path, "/api/health")
            // A page opened is a page opened, whoever opened it: the visit
            // beacon fires on every navigation and must not be refused.
            || Requests.StartsWithSegments(path, "/api/activity");
    }

    /// <summary>
    /// What the gate says it is waiting for. Pure so the wording is testable
    /// and so the one portal-shaped decision in it — whether a phone is
    /// something this deployment can even use — is visible.
    /// </summary>
    public static string GateMessage(bool canText) => canText
        ? "Confirm your email address or phone number to continue."
        : "Confirm your email address to continue.";
}
