using MimeKit;
using MMail.Core.Models;

namespace MMail.Core.Mail;

/// A server mailbox and, for the special ones, the standard folder it plays
/// (inbox, sent, drafts, trash, spam, archive, all).
public sealed record MailboxInfo(string Name, string? Role);

/// What the list needs about a message, without its body.
public sealed record MessageHeader(
    uint Uid, DateTimeOffset Date, string FromName, string FromEmail, IReadOnlyList<string> To,
    IReadOnlyList<string> Cc, string Subject, bool Seen, bool Flagged, IReadOnlyList<string> Keywords,
    string? MessageId, string? InReplyTo, string? References, string Preview, bool HasAttachments);

public enum MailFlag { Seen, Flagged, Deleted }

/// Everything MMail asks of a mail server. The real one is <see cref="MailKitService"/>;
/// tests use a fake.
public interface IMailService
{
    Task VerifyAsync(MailAccountConfig cfg, string imapPassword, string smtpPassword, CancellationToken ct = default);
    Task<IReadOnlyList<MailboxInfo>> ListMailboxesAsync(MailAccountConfig cfg, CancellationToken ct = default);
    /// The newest messages of a mailbox, newest first; only those above <paramref name="belowUid"/> when given.
    Task<IReadOnlyList<MessageHeader>> FetchHeadersAsync(MailAccountConfig cfg, string mailbox, int limit,
                                                         uint? belowUid = null, CancellationToken ct = default);
    Task<IReadOnlyList<MessageHeader>> SearchAsync(MailAccountConfig cfg, string mailbox, string text, int limit,
                                                   CancellationToken ct = default);
    Task<MimeMessage> FetchMessageAsync(MailAccountConfig cfg, string mailbox, uint uid, CancellationToken ct = default);
    Task SetFlagAsync(MailAccountConfig cfg, string mailbox, uint uid, MailFlag flag, bool add, CancellationToken ct = default);
    Task SetKeywordAsync(MailAccountConfig cfg, string mailbox, uint uid, string keyword, bool add, CancellationToken ct = default);
    Task MoveAsync(MailAccountConfig cfg, string fromMailbox, uint uid, string toMailbox, CancellationToken ct = default);
    Task AppendAsync(MailAccountConfig cfg, string mailbox, MimeMessage message, bool seen, bool draft, CancellationToken ct = default);
    Task SendAsync(MailAccountConfig cfg, MimeMessage message, CancellationToken ct = default);
    void Forget(string accountId);
}

/// Where passwords live. On Windows: the Credential Manager.
public interface ICredentialStore
{
    string? Get(string key);
    void Set(string key, string secret);
    void Delete(string key);
}

/// In-memory store (tests, or when no secure store is available).
public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _items = [];
    public string? Get(string key) => _items.GetValueOrDefault(key);
    public void Set(string key, string secret) => _items[key] = secret;
    public void Delete(string key) => _items.Remove(key);
}
