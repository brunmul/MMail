using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MMail.Core.Models;

namespace MMail.Core.Mail;

/// IMAP/SMTP through MailKit. One long-lived IMAP connection per account,
/// used by one caller at a time.
public sealed class MailKitService(ICredentialStore credentials) : IMailService
{
    private sealed class Conn
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public ImapClient? Client;
    }

    private readonly Dictionary<string, Conn> _conns = [];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static SecureSocketOptions Options(ConnectionSecurity s) => s switch
    {
        ConnectionSecurity.Tls => SecureSocketOptions.SslOnConnect,
        ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };

    public async Task VerifyAsync(MailAccountConfig cfg, string imapPassword, string smtpPassword, CancellationToken ct = default)
    {
        using (var imap = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds })
        {
            await imap.ConnectAsync(cfg.ImapHost, cfg.ImapPort, Options(cfg.ImapSecurity), ct);
            await imap.AuthenticateAsync(cfg.ImapUsername, imapPassword, ct);
            await imap.DisconnectAsync(true, ct);
        }
        using var smtp = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        await smtp.ConnectAsync(cfg.SmtpHost, cfg.SmtpPort, Options(cfg.SmtpSecurity), ct);
        await smtp.AuthenticateAsync(cfg.SmtpUsername, smtpPassword, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    private async Task<T> WithClient<T>(MailAccountConfig cfg, Func<ImapClient, Task<T>> work, CancellationToken ct)
    {
        Conn conn;
        lock (_conns)
        {
            if (!_conns.TryGetValue(cfg.Id, out conn!)) _conns[cfg.Id] = conn = new Conn();
        }
        await conn.Gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var client = conn.Client;
                    if (client is null || !client.IsConnected || !client.IsAuthenticated)
                    {
                        client?.Dispose();
                        client = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds };
                        await client.ConnectAsync(cfg.ImapHost, cfg.ImapPort, Options(cfg.ImapSecurity), ct);
                        var password = credentials.Get(cfg.ImapPasswordKey)
                                       ?? throw new InvalidOperationException($"No password saved for {cfg.Email}.");
                        await client.AuthenticateAsync(cfg.ImapUsername, password, ct);
                        conn.Client = client;
                    }
                    return await work(client);
                }
                catch (Exception ex) when (attempt == 0 && ex is IOException or ImapProtocolException or ServiceNotConnectedException)
                {
                    // A dropped connection: reconnect once.
                    conn.Client?.Dispose();
                    conn.Client = null;
                }
            }
        }
        finally
        {
            conn.Gate.Release();
        }
    }

    private Task WithClient(MailAccountConfig cfg, Func<ImapClient, Task> work, CancellationToken ct) =>
        WithClient<bool>(cfg, async c => { await work(c); return true; }, ct);

    public Task<IReadOnlyList<MailboxInfo>> ListMailboxesAsync(MailAccountConfig cfg, CancellationToken ct = default) =>
        WithClient<IReadOnlyList<MailboxInfo>>(cfg, async client =>
        {
            var list = new List<MailboxInfo> { new(client.Inbox.FullName, "inbox") };
            foreach (var ns in client.PersonalNamespaces)
            {
                var folders = await client.GetFoldersAsync(ns, false, ct);
                foreach (var f in folders)
                {
                    if (f.Attributes.HasFlag(FolderAttributes.NonExistent) || f.Attributes.HasFlag(FolderAttributes.NoSelect)) continue;
                    if (f.FullName.Equals(client.Inbox.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new MailboxInfo(f.FullName, Role(f)));
                }
            }
            return list;
        }, ct);

    private static string? Role(IMailFolder f)
    {
        var a = f.Attributes;
        if (a.HasFlag(FolderAttributes.Sent)) return "sent";
        if (a.HasFlag(FolderAttributes.Drafts)) return "drafts";
        if (a.HasFlag(FolderAttributes.Trash)) return "trash";
        if (a.HasFlag(FolderAttributes.Junk)) return "spam";
        if (a.HasFlag(FolderAttributes.Archive)) return "archive";
        if (a.HasFlag(FolderAttributes.All)) return "all";
        if (a.HasFlag(FolderAttributes.Flagged)) return "starred";
        return f.Name.ToLowerInvariant() switch
        {
            "sent" or "sent items" or "sent mail" => "sent",
            "drafts" => "drafts",
            "trash" or "deleted items" or "bin" => "trash",
            "spam" or "junk" or "junk e-mail" => "spam",
            "archive" or "archives" => "archive",
            _ => null,
        };
    }

    private const MessageSummaryItems HeaderItems = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope
        | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure
        | MessageSummaryItems.PreviewText | MessageSummaryItems.References;

    public Task<IReadOnlyList<MessageHeader>> FetchHeadersAsync(MailAccountConfig cfg, string mailbox, int limit,
                                                                uint? belowUid = null, CancellationToken ct = default) =>
        WithClient<IReadOnlyList<MessageHeader>>(cfg, async client =>
        {
            var folder = await client.GetFolderAsync(mailbox, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            var query = belowUid is { } below && below > 1
                ? SearchQuery.Uids(new UniqueIdRange(new UniqueId(1), new UniqueId(below - 1)))
                : SearchQuery.All;
            var uids = await folder.SearchAsync(query, ct);
            var wanted = uids.OrderByDescending(u => u.Id).Take(limit).ToList();
            if (wanted.Count == 0) return [];
            var summaries = await folder.FetchAsync(wanted, HeaderItems, ct);
            return summaries.Select(ToHeader).OrderByDescending(h => h.Uid).ToList();
        }, ct);

    public Task<IReadOnlyList<MessageHeader>> SearchAsync(MailAccountConfig cfg, string mailbox, string text, int limit,
                                                          CancellationToken ct = default) =>
        WithClient<IReadOnlyList<MessageHeader>>(cfg, async client =>
        {
            var folder = await client.GetFolderAsync(mailbox, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            SearchQuery query = client.Capabilities.HasFlag(ImapCapabilities.GMailExt1)
                ? SearchQuery.GMailRawSearch(text)
                : SearchQuery.SubjectContains(text).Or(SearchQuery.FromContains(text)).Or(SearchQuery.BodyContains(text));
            var uids = await folder.SearchAsync(query, ct);
            var wanted = uids.OrderByDescending(u => u.Id).Take(limit).ToList();
            if (wanted.Count == 0) return [];
            var summaries = await folder.FetchAsync(wanted, HeaderItems, ct);
            return summaries.Select(ToHeader).OrderByDescending(h => h.Uid).ToList();
        }, ct);

    private static MessageHeader ToHeader(IMessageSummary s)
    {
        var env = s.Envelope;
        var from = env?.From.Mailboxes.FirstOrDefault();
        var flags = s.Flags ?? MessageFlags.None;
        return new MessageHeader(
            s.UniqueId.Id,
            s.InternalDate ?? env?.Date ?? DateTimeOffset.Now,
            from?.Name ?? "", from?.Address ?? "",
            env?.To.Mailboxes.Select(m => m.ToString()).ToList() ?? [],
            env?.Cc.Mailboxes.Select(m => m.ToString()).ToList() ?? [],
            env?.Subject ?? "",
            flags.HasFlag(MessageFlags.Seen), flags.HasFlag(MessageFlags.Flagged),
            s.Keywords?.Select(k => k.ToLowerInvariant()).ToList() ?? [],
            env?.MessageId, env?.InReplyTo,
            s.References is { Count: > 0 } refs ? string.Join(' ', refs.Select(r => $"<{r}>")) : null,
            MimeTools.OneLine(s.PreviewText ?? ""),
            s.Attachments?.Any() ?? false);
    }

    public Task<MimeMessage> FetchMessageAsync(MailAccountConfig cfg, string mailbox, uint uid, CancellationToken ct = default) =>
        WithClient(cfg, async client =>
        {
            var folder = await client.GetFolderAsync(mailbox, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            return await folder.GetMessageAsync(new UniqueId(uid), ct);
        }, ct);

    public Task SetFlagAsync(MailAccountConfig cfg, string mailbox, uint uid, MailFlag flag, bool add, CancellationToken ct = default) =>
        WithClient(cfg, async client =>
        {
            var folder = await client.GetFolderAsync(mailbox, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            var f = flag switch { MailFlag.Seen => MessageFlags.Seen, MailFlag.Flagged => MessageFlags.Flagged, _ => MessageFlags.Deleted };
            var request = new StoreFlagsRequest(add ? StoreAction.Add : StoreAction.Remove, f) { Silent = true };
            await folder.StoreAsync(new UniqueId(uid), request, ct);
            if (flag == MailFlag.Deleted && add) await folder.ExpungeAsync([new UniqueId(uid)], ct);
        }, ct);

    public Task SetKeywordAsync(MailAccountConfig cfg, string mailbox, uint uid, string keyword, bool add, CancellationToken ct = default) =>
        WithClient(cfg, async client =>
        {
            var folder = await client.GetFolderAsync(mailbox, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            var request = new StoreFlagsRequest(add ? StoreAction.Add : StoreAction.Remove, [keyword]) { Silent = true };
            await folder.StoreAsync(new UniqueId(uid), request, ct);
        }, ct);

    public Task MoveAsync(MailAccountConfig cfg, string fromMailbox, uint uid, string toMailbox, CancellationToken ct = default) =>
        WithClient(cfg, async client =>
        {
            var from = await client.GetFolderAsync(fromMailbox, ct);
            var to = await client.GetFolderAsync(toMailbox, ct);
            await from.OpenAsync(FolderAccess.ReadWrite, ct);
            await from.MoveToAsync(new UniqueId(uid), to, ct);
        }, ct);

    public Task AppendAsync(MailAccountConfig cfg, string mailbox, MimeMessage message, bool seen, bool draft, CancellationToken ct = default) =>
        WithClient(cfg, async client =>
        {
            var folder = await client.GetFolderAsync(mailbox, ct);
            var flags = (seen ? MessageFlags.Seen : MessageFlags.None) | (draft ? MessageFlags.Draft : MessageFlags.None);
            await folder.AppendAsync(new AppendRequest(message, flags), ct);
        }, ct);

    public async Task SendAsync(MailAccountConfig cfg, MimeMessage message, CancellationToken ct = default)
    {
        var password = credentials.Get(cfg.SmtpPasswordKey) ?? credentials.Get(cfg.ImapPasswordKey)
                       ?? throw new InvalidOperationException($"No password saved for {cfg.Email}.");
        using var smtp = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        await smtp.ConnectAsync(cfg.SmtpHost, cfg.SmtpPort, Options(cfg.SmtpSecurity), ct);
        await smtp.AuthenticateAsync(cfg.SmtpUsername, password, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    public void Forget(string accountId)
    {
        lock (_conns)
        {
            if (_conns.Remove(accountId, out var conn)) conn.Client?.Dispose();
        }
    }
}
