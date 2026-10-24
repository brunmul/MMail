using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MMail.Core.Models;

namespace MMail.Core.Mail;

/// What MMail keeps of a parsed message.
public sealed record ParsedMessage(string Text, string? Html, IReadOnlyList<ComposeAttachment> Attachments,
                                   string? ListUnsubscribe, CalendarEvent? Calendar);

public static partial class MimeTools
{
    public static ParsedMessage Parse(MimeMessage message)
    {
        var html = message.HtmlBody;
        var text = message.TextBody;
        if (string.IsNullOrWhiteSpace(text)) text = html is null ? "" : HtmlToText(html);

        var attachments = new List<ComposeAttachment>();
        foreach (var part in message.Attachments)
        {
            using var ms = new MemoryStream();
            string name;
            if (part is MimePart mp)
            {
                mp.Content?.DecodeTo(ms);
                name = mp.FileName ?? "attachment";
            }
            else if (part is MessagePart msg)
            {
                msg.Message?.WriteTo(ms);
                name = (msg.Message?.Subject ?? "message") + ".eml";
            }
            else continue;
            attachments.Add(new ComposeAttachment(name, part.ContentType.MimeType, ms.ToArray()));
        }

        CalendarEvent? calendar = null;
        var ics = message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.ContentType.IsMimeType("text", "calendar"));
        if (ics is not null) calendar = ParseIcs(ics.Text);

        return new ParsedMessage(text.Trim(), html, attachments, message.Headers[HeaderId.ListUnsubscribe], calendar);
    }

    /// Readable text from HTML: drops head, style, script and comments, keeps line breaks.
    public static string HtmlToText(string html)
    {
        var s = HiddenBlocks().Replace(html, "");
        s = LineBreaks().Replace(s, "\n");
        s = Tags().Replace(s, "");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');
        s = SpaceRuns().Replace(s, " ");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    /// One line of at most <paramref name="max"/> characters, for list previews.
    public static string OneLine(string text, int max = 140)
    {
        var s = SpaceRuns().Replace(text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), " ").Trim();
        return s.Length <= max ? s : s[..max];
    }

    public static MimeMessage Build(MailAccountConfig from, string? displayName, ComposeDraft draft)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(displayName ?? from.DisplayName, from.Email));
        AddAll(message.To, draft.To);
        AddAll(message.Cc, draft.Cc);
        AddAll(message.Bcc, draft.Bcc);
        message.Subject = draft.Subject;
        message.Date = DateTimeOffset.Now;
        if (!string.IsNullOrEmpty(draft.InReplyTo)) message.InReplyTo = draft.InReplyTo.Trim('<', '>');
        if (!string.IsNullOrEmpty(draft.References))
            foreach (var r in draft.References.Split(' ', StringSplitOptions.RemoveEmptyEntries)) message.References.Add(r.Trim('<', '>'));

        var builder = new BodyBuilder { TextBody = draft.Body };
        foreach (var a in draft.Attachments)
            builder.Attachments.Add(a.FileName, a.Data, ContentType.Parse(a.MimeType));
        message.Body = builder.ToMessageBody();
        return message;
    }

    private static void AddAll(InternetAddressList list, string field)
    {
        foreach (var piece in field.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (MailboxAddress.TryParse(piece, out var address)) list.Add(address);
    }

    /// The addresses in a To/Cc field ("Ann <ann@x.com>, bob@y.com" → ann@x.com, bob@y.com).
    public static List<string> ParseRecipients(string field) =>
        field.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(p =>
             {
                 int lt = p.IndexOf('<'), gt = p.IndexOf('>');
                 return lt >= 0 && gt > lt ? p[(lt + 1)..gt].Trim() : p;
             })
             .Where(p => p.Contains('@'))
             .ToList();

    public static string Quote(Email original)
    {
        var when = original.Date.ToString("ddd d MMM yyyy HH:mm", CultureInfo.CurrentCulture);
        var quoted = string.Join('\n', original.Body.Split('\n').Select(l => "> " + l.TrimEnd('\r')));
        return $"\n\nOn {when}, {original.FromDisplay} wrote:\n{quoted}";
    }

    public static CalendarEvent? ParseIcs(string raw)
    {
        // Unfold continuation lines first (RFC 5545 §3.1).
        var lines = raw.Replace("\r\n ", "").Replace("\n ", "").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        string? Get(string key) => lines.FirstOrDefault(l => l.StartsWith(key + ":") || l.StartsWith(key + ";")) is { } line
            ? line[(line.IndexOf(':') + 1)..].Replace("\\,", ",").Replace("\\n", "\n")
            : null;
        var summary = Get("SUMMARY");
        if (summary is null) return null;
        var organizer = Get("ORGANIZER");
        if (organizer?.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) == true) organizer = organizer[7..];
        return new CalendarEvent(summary, IcsDate(Get("DTSTART")), IcsDate(Get("DTEND")), Get("LOCATION"), organizer, raw);
    }

    private static DateTimeOffset? IcsDate(string? v)
    {
        if (v is null) return null;
        string[] formats = ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd"];
        var style = v.EndsWith('Z') ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal;
        return DateTimeOffset.TryParseExact(v, formats, CultureInfo.InvariantCulture, style, out var d) ? d : null;
    }

    private static readonly Dictionary<string, string> MimeTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["txt"] = "text/plain", ["md"] = "text/markdown", ["csv"] = "text/csv", ["html"] = "text/html",
        ["ics"] = "text/calendar", ["json"] = "application/json", ["xml"] = "application/xml",
        ["pdf"] = "application/pdf", ["zip"] = "application/zip", ["png"] = "image/png", ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg", ["gif"] = "image/gif", ["heic"] = "image/heic", ["webp"] = "image/webp",
        ["svg"] = "image/svg+xml", ["doc"] = "application/msword",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    public static string GuessMime(string fileName) =>
        MimeTable.GetValueOrDefault(Path.GetExtension(fileName).TrimStart('.'), "application/octet-stream");

    public static bool IsText(string mime) => mime.StartsWith("text/") || mime is "application/json" or "application/xml";

    /// HTML ready for the reader: remote content blocked by a strict CSP, links kept.
    public static string SafeHtml(string html, bool allowRemoteImages)
    {
        var img = allowRemoteImages ? "data: cid: https: http:" : "data: cid:";
        var csp = $"<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src {img}; style-src 'unsafe-inline'; font-src data:\">";
        var head = "<meta charset=\"utf-8\">" + csp +
                   "<style>body{font-family:'Segoe UI',sans-serif;font-size:14px;color:#262624;margin:0;padding:8px 4px;word-wrap:break-word}" +
                   "img{max-width:100%;height:auto}a{color:#C15F3C}</style>";
        var withoutScripts = Scripts().Replace(html, "");
        var m = HeadOpen().Match(withoutScripts);
        return m.Success ? withoutScripts.Insert(m.Index + m.Length, head) : $"<!doctype html><html><head>{head}</head><body>{withoutScripts}</body></html>";
    }

    /// The text body as HTML, for the same reader.
    public static string TextAsHtml(string text) =>
        SafeHtml("<body><div style=\"white-space:pre-wrap\">" + WebUtility.HtmlEncode(text) + "</div></body>", false);

    public static int CountRemoteImages(string html) => RemoteImages().Matches(html).Count;

    [GeneratedRegex(@"<(head|style|script|title)\b[^>]*>.*?</\1\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();
    [GeneratedRegex(@"<br\s*/?>|</(p|div|tr|li|h[1-6]|blockquote)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();
    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex SpaceRuns();
    [GeneratedRegex(@"\n\s*\n\s*(\n\s*)+")]
    private static partial Regex BlankLines();
    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Scripts();
    [GeneratedRegex(@"<head\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadOpen();
    [GeneratedRegex(@"<img\b[^>]*\bsrc\s*=\s*[""']?https?:", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteImages();
}
