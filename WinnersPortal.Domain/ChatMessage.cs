namespace WinnersPortal.Domain;

/// <summary>
/// One line of the conversation between an opportunity's client and one of
/// its entrants. The conversation is the entry: an <see cref="Entry"/> already
/// names the one freelancer and, through its opportunity, the one client, so
/// a message needs no room of its own — and nobody outside that pair is ever
/// party to it. Written once, never edited; read by the other side, which
/// stamps <see cref="ReadAtUtc"/>. Sent nowhere else: the dock and the
/// Messages page are where it is read.
/// </summary>
public sealed class ChatMessage
{
    // Column limit: the one place the length is stated; the rules and the
    // DbContext both read it here. Long enough for a paragraph with a
    // link in it, short enough that a brief stays on the opportunity page.
    public const int MaxBodyLength = 2000;

    public Guid Id { get; set; }

    public Guid EntryId { get; set; }
    public Entry? Entry { get; set; }

    /// <summary>The entrant or the client — the entry says which is which.</summary>
    public Guid SenderId { get; set; }
    public User? Sender { get; set; }

    public required string Body { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When the other side opened the conversation after this arrived; null while unread.</summary>
    public DateTimeOffset? ReadAtUtc { get; set; }
}
