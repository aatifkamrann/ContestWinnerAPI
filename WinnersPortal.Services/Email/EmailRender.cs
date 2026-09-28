using System.Text;
using System.Text.RegularExpressions;

namespace WinnersPortal.Services.Email;

/// <summary>
/// Renders a queued message into its two MIME parts at send time. Stored
/// messages are text paragraphs plus a relative action path, so the layout
/// and the portal's public URL are applied when the mail goes out — a fixed
/// typo or a corrected publicUrl reaches everything still in the queue.
/// </summary>
public static partial class EmailRender
{
    [GeneratedRegex(@"https?://[^\s<]+")]
    private static partial Regex Url();

    /// <summary>The foot of every opted-in email: why it came, and the one click that stops it.</summary>
    public const string UnsubscribeLine =
        "You get this because you asked for it under Notifications. One click stops these emails:";

    public static string AbsoluteUrl(string publicBaseUrl, string actionPath) =>
        publicBaseUrl.TrimEnd('/') + actionPath;

    public static string Text(
        string toName, string textBody, string? actionText, string? actionUrl, string portalName,
        string? unsubscribeUrl = null)
    {
        var sb = new StringBuilder();
        sb.Append("Hi ").Append(toName).Append(",\n\n").Append(textBody);
        if (actionText is not null && actionUrl is not null)
            sb.Append("\n\n").Append(actionText).Append(": ").Append(actionUrl);
        if (unsubscribeUrl is not null)
            sb.Append("\n\n").Append(UnsubscribeLine).Append(' ').Append(unsubscribeUrl);
        sb.Append("\n\n— ").Append(portalName);
        return sb.ToString();
    }

    /// <summary>
    /// A deliberately minimal HTML alternative: one column, system fonts, the
    /// portal accent on the single button. Everything interpolated is escaped;
    /// bare URLs in the text become links afterwards, so escaping cannot break
    /// an href and content cannot inject markup.
    /// </summary>
    public static string Html(
        string toName, string textBody, string? actionText, string? actionUrl,
        string portalName, string accentColor, string? unsubscribeUrl = null)
    {
        // The accent comes from a setting; anything that doesn't look like a
        // colour falls back rather than landing inside a style attribute.
        if (!Regex.IsMatch(accentColor, "^#[0-9a-fA-F]{6}$")) accentColor = "#0f766e";

        var paragraphs = textBody
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => "<p style=\"margin:0 0 14px;line-height:1.55;\">"
                + Linkify(Escape(p.Trim())).Replace("\n", "<br>") + "</p>");

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><body style=\"margin:0;padding:0;background:#f4f5f7;\">");
        sb.Append("<div style=\"max-width:560px;margin:0 auto;padding:32px 20px;")
          .Append("font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;")
          .Append("font-size:15px;color:#1f2937;\">");
        sb.Append("<div style=\"background:#ffffff;border-radius:10px;padding:28px 28px 22px;")
          .Append("border:1px solid #e5e7eb;\">");
        sb.Append("<p style=\"margin:0 0 14px;line-height:1.55;\">Hi ").Append(Escape(toName)).Append(",</p>");
        foreach (var p in paragraphs) sb.Append(p);
        if (actionText is not null && actionUrl is not null)
        {
            sb.Append("<p style=\"margin:22px 0 8px;\"><a href=\"").Append(Escape(actionUrl)).Append('"')
              .Append(" style=\"display:inline-block;background:").Append(accentColor)
              .Append(";color:#ffffff;text-decoration:none;padding:10px 18px;border-radius:8px;")
              .Append("font-weight:600;\">").Append(Escape(actionText)).Append("</a></p>");
        }
        sb.Append("</div>");
        sb.Append("<p style=\"margin:14px 4px 0;font-size:13px;color:#6b7280;\">— ")
          .Append(Escape(portalName)).Append("</p>");
        if (unsubscribeUrl is not null)
            sb.Append("<p style=\"margin:6px 4px 0;font-size:12px;color:#9ca3af;\">")
              .Append("You get this because you asked for it under Notifications. <a href=\"")
              .Append(Escape(unsubscribeUrl))
              .Append("\" style=\"color:inherit;\">One click stops these emails</a>.</p>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    public static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private static string Linkify(string escaped) =>
        Url().Replace(escaped, m => $"<a href=\"{m.Value}\" style=\"color:inherit;\">{m.Value}</a>");
}
