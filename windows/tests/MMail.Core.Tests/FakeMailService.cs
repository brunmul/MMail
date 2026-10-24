using MimeKit;
using MMail.Core.Mail;
using MMail.Core.Models;

namespace MMail.Core.Tests;

/// A server in memory: mailboxes of messages, every call recorded.
public sealed class FakeMailService : IMailService
{
    public Dictionary<string, List<(MessageHeader Header, MimeMessage Message)>> Boxes { get; } = new()
    {
        ["INBOX"] = [], ["[Gmail]/All Mail"] = [], ["[Gmail]/Sent Mail"] = [], ["[Gmail]/Drafts"] = [],
        ["[Gmail]/Trash"] = [], ["[Gmail]/Spam"] = [], ["Receipts"] = [],
    };
    public List<string> Calls { get; } = [];
    public List<MimeMessage> Sent { get; } = [];
    private uint _nextUid = 100;

    public MessageHeader Deliver(string box, string fromName, string fromEmail, string subject, string body,
                                 bool seen = false, DateTimeOffset? date = null, params (string Name, string Text)[] attachments)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromEmail));
        message.To.Add(new MailboxAddress("Me", "me@gmail.com"));
        message.Subject = subject;
        message.MessageId = $"m{_nextUid}@test";
        var builder = new BodyBuilder { TextBody = body };
        foreach (var (name, text) in attachments) builder.Attachments.Add(name, System.Text.Encoding.UTF8.GetBytes(text), ContentType.Parse("text/plain"));
        message.Body = builder.ToMessageBody();
        var header = new MessageHeader(_nextUid++, date ?? DateTimeOffset.Now, fromName, fromEmail, ["Me <me@gmail.com>"], [],
                                       subject, seen, false, [], message.MessageId, null, null, MimeTools.OneLine(body),
                                       attachments.Length > 0);
        Boxes[box].Add((header, message));
        return header;
    }

    public Task VerifyAsync(MailAccountConfig cfg, string imapPassword, string smtpPassword, CancellationToken ct = default)
    {
        Calls.Add($"verify {cfg.Email}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MailboxInfo>> ListMailboxesAsync(MailAccountConfig cfg, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MailboxInfo>>(
        [
            new("INBOX", "inbox"), new("[Gmail]/All Mail", "all"), new("[Gmail]/Sent Mail", "sent"),
            new("[Gmail]/Drafts", "drafts"), new("[Gmail]/Trash", "trash"), new("[Gmail]/Spam", "spam"), new("Receipts", null),
        ]);

    public Task<IReadOnlyList<MessageHeader>> FetchHeadersAsync(MailAccountConfig cfg, string mailbox, int limit, uint? belowUid = null,
                                                                CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MessageHeader>>(Boxes[mailbox].Select(m => m.Header)
            .Where(h => belowUid is null || h.Uid < belowUid).OrderByDescending(h => h.Uid).Take(limit).ToList());

    public Task<IReadOnlyList<MessageHeader>> SearchAsync(MailAccountConfig cfg, string mailbox, string text, int limit,
                                                          CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MessageHeader>>(Boxes[mailbox]
            .Where(m => m.Header.Subject.Contains(text, StringComparison.OrdinalIgnoreCase) || (m.Message.TextBody ?? "").Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Header).Take(limit).ToList());

    public Task<MimeMessage> FetchMessageAsync(MailAccountConfig cfg, string mailbox, uint uid, CancellationToken ct = default) =>
        Task.FromResult(Boxes[mailbox].First(m => m.Header.Uid == uid).Message);

    public Task SetFlagAsync(MailAccountConfig cfg, string mailbox, uint uid, MailFlag flag, bool add, CancellationToken ct = default)
    {
        Calls.Add($"flag {mailbox} {uid} {flag} {add}");
        return Task.CompletedTask;
    }

    public Task SetKeywordAsync(MailAccountConfig cfg, string mailbox, uint uid, string keyword, bool add, CancellationToken ct = default)
    {
        Calls.Add($"keyword {mailbox} {uid} {keyword} {add}");
        return Task.CompletedTask;
    }

    public Task MoveAsync(MailAccountConfig cfg, string fromMailbox, uint uid, string toMailbox, CancellationToken ct = default)
    {
        Calls.Add($"move {fromMailbox} {uid} {toMailbox}");
        var m = Boxes[fromMailbox].First(x => x.Header.Uid == uid);
        Boxes[fromMailbox].Remove(m);
        Boxes[toMailbox].Add(m);
        return Task.CompletedTask;
    }

    public Task AppendAsync(MailAccountConfig cfg, string mailbox, MimeMessage message, bool seen, bool draft, CancellationToken ct = default)
    {
        Calls.Add($"append {mailbox}");
        return Task.CompletedTask;
    }

    public Task SendAsync(MailAccountConfig cfg, MimeMessage message, CancellationToken ct = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }

    public void Forget(string accountId) { }
}
