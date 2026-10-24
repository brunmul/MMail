using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MMail.Core.Models;

public enum ConnectionSecurity { Tls, StartTls, None }

/// How an account logs in: a password (app password for Gmail), or "Sign in with Google",
/// where the credential store keeps Google's refresh token instead of a password.
public enum AuthKind { Password, Google }

/// Connection settings for an IMAP/SMTP account. Passwords live in the
/// credential store, keyed by <see cref="ImapPasswordKey"/> / <see cref="SmtpPasswordKey"/>.
public sealed class MailAccountConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";

    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public ConnectionSecurity ImapSecurity { get; set; } = ConnectionSecurity.Tls;
    public string ImapUsername { get; set; } = "";

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public ConnectionSecurity SmtpSecurity { get; set; } = ConnectionSecurity.Tls;
    public string SmtpUsername { get; set; } = "";

    public AuthKind Auth { get; set; } = AuthKind.Password;

    public string ImapPasswordKey => $"{Id}.imap";
    public string SmtpPasswordKey => $"{Id}.smtp";

    public bool IsGmail => ImapHost.EndsWith("gmail.com", StringComparison.OrdinalIgnoreCase)
                           || ImapHost.EndsWith("googlemail.com", StringComparison.OrdinalIgnoreCase);

    /// Gmail by app password: imap.gmail.com:993 TLS, smtp.gmail.com:465 TLS.
    public static MailAccountConfig Gmail(string displayName, string email) => new()
    {
        DisplayName = displayName, Email = email,
        ImapHost = "imap.gmail.com", ImapPort = 993, ImapSecurity = ConnectionSecurity.Tls, ImapUsername = email,
        SmtpHost = "smtp.gmail.com", SmtpPort = 465, SmtpSecurity = ConnectionSecurity.Tls, SmtpUsername = email,
    };
}

public sealed record AttachmentMeta(string FileName, string MimeType, long Size);

public sealed record MailLabel(string Id, string Name, string ColorHex);

public sealed record CalendarEvent(string Summary, DateTimeOffset? Start, DateTimeOffset? End,
                                   string? Location, string? Organizer, string Raw);

/// One message, as cached locally and shown in the list. Mutable fields raise
/// change notifications so the list and the reader update in place.
public sealed class Email : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Account { get; init; }
    public string Folder { get => _folder; set => Set(ref _folder, value); }
    public uint Uid { get; set; }

    public string FromName { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public List<string> To { get; set; } = [];
    public List<string> Cc { get; set; } = [];
    public string Subject { get; set; } = "";
    public string Preview { get => _preview; set => Set(ref _preview, value); }
    public string Body { get => _body; set => Set(ref _body, value); }
    public string? BodyHtml { get => _bodyHtml; set => Set(ref _bodyHtml, value); }
    public DateTimeOffset Date { get; set; }

    public bool Unread { get => _unread; set => Set(ref _unread, value); }
    public bool Starred { get => _starred; set => Set(ref _starred, value); }
    public bool HasAttachment { get => _hasAttachment; set => Set(ref _hasAttachment, value); }
    public List<string> Labels { get; set; } = [];
    public List<AttachmentMeta> Attachments { get; set; } = [];

    /// Whether <see cref="Body"/> holds the whole message (not just the server preview).
    public bool BodyLoaded { get => _bodyLoaded; set => Set(ref _bodyLoaded, value); }
    public string? MessageId { get; set; }
    public string? InReplyTo { get; set; }
    public string? References { get; set; }
    public string? Unsubscribe { get; set; }
    public CalendarEvent? CalendarEvent { get; set; }

    public string SenderName => string.IsNullOrWhiteSpace(FromName) ? (FromEmail.Length > 0 ? FromEmail : "(unknown)") : FromName;
    public string SenderInitials => string.Concat(SenderName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public string FromDisplay => FromEmail.Length == 0 ? SenderName : $"{SenderName} <{FromEmail}>";
    public bool LooksAutomated => Sender.LooksAutomated(FromEmail);

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Touch(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private string _folder = "inbox", _preview = "", _body = "";
    private string? _bodyHtml;
    private bool _unread, _starred, _hasAttachment, _bodyLoaded;

    public static string MakeId(string account, string folder, uint uid) => $"{account}#{folder}#{uid}";
}

public static class Sender
{
    private static readonly string[] Palette =
        ["E5484D", "1FB36B", "7A5AE0", "F4A52A", "2D3DEC", "0EA5E9", "D946EF", "06B6D4", "B25A2A", "635BFF"];

    /// Deterministic avatar color from an address, so a contact keeps one color.
    public static string StableColorHex(string key)
    {
        ulong hash = 5381;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(key)) hash = unchecked(hash * 33 + b);
        return Palette[(int)(hash % (ulong)Palette.Length)];
    }

    public static bool LooksAutomated(string email)
    {
        var local = email.Split('@')[0].ToLowerInvariant();
        string[] markers = ["noreply", "no-reply", "donotreply", "notifications", "notification",
                            "automated", "mailer", "support", "info", "hello", "team"];
        return markers.Any(local.Contains);
    }
}

public sealed record ComposeAttachment(string FileName, string MimeType, byte[] Data);

/// The composer's contents. The window binds to it two-way, so what Claude reads
/// with get_compose always includes the user's own edits.
public sealed class ComposeDraft : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public string To { get => _to; set => Set(ref _to, value); }
    public string Cc { get => _cc; set => Set(ref _cc, value); }
    public string Bcc { get => _bcc; set => Set(ref _bcc, value); }
    public string Subject { get => _subject; set => Set(ref _subject, value); }
    public string Body { get => _body; set => Set(ref _body, value); }
    public string FromId { get => _fromId; set => Set(ref _fromId, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public System.Collections.ObjectModel.ObservableCollection<ComposeAttachment> Attachments { get; } = [];

    public string? InReplyTo { get; set; }
    public string? References { get; set; }
    /// The Drafts message this composer replaces, if any.
    public string? OriginalDraftId { get; set; }

    public bool IsBlank => To.Length == 0 && Subject.Length == 0 && Body.Trim().Length == 0 && Attachments.Count == 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private string _to = "", _cc = "", _bcc = "", _subject = "", _body = "", _fromId = "", _title = "New message";
}

public enum InboxFilter { All, Unread, People, Updates }

/// Where a row shows in the inbox: Pinned, then Unread (All tab only), then the rest.
public enum ListSection { Pinned = 0, Unread = 1, Rest = 2 }

public static class Folders
{
    /// The standard folders, in sidebar order. "done" is accepted as an alias of archive.
    public static readonly string[] Standard = ["inbox", "starred", "snoozed", "archive", "sent", "drafts", "spam", "trash"];

    public static string Title(string id) => id switch
    {
        "inbox" => "Inbox", "starred" => "Starred", "snoozed" => "Snoozed", "archive" => "Archive",
        "sent" => "Sent", "drafts" => "Drafts", "spam" => "Spam", "trash" => "Trash", _ => id,
    };

    public static string Normalize(string id) => id == "done" ? "archive" : id;

    /// Folders that live on the server as their own mailbox (starred and snoozed are views).
    public static bool IsServerFolder(string id) => id is not ("starred" or "snoozed");
}

public sealed record ToastMessage(string Text, string? ActionLabel = null, Action? Action = null, TimeSpan? Duration = null);

public sealed record Contact(string Name, string Email);
