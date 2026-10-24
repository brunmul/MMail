using MMail.Core.Mail;
using MMail.Core.Models;
using MMail.Core.Storage;

namespace MMail.Core.State;

/// The app's state and every action on it, shared by the window and by Claude
/// (through the MCP tools), so whatever Claude does shows up on screen.
/// Not thread-safe: call it from the UI thread.
public sealed class MailController
{
    private readonly MailStore _store;
    private readonly IMailService _mail;
    private readonly ICredentialStore _credentials;

    public MailController(MailStore store, IMailService mail, ICredentialStore credentials)
    {
        _store = store;
        _mail = mail;
        _credentials = credentials;
        Accounts = store.LoadAccounts();
        PinnedIds = store.GetPref<HashSet<string>>("pinned") ?? [];
        Snoozed = store.GetPref<Dictionary<string, DateTimeOffset>>("snoozed") ?? [];
        Labels = store.GetPref<List<MailLabel>>("labels") ?? [];
        Signatures = store.GetPref<Dictionary<string, string>>("signatures") ?? [];
        TrustedImageSenders = store.GetPref<HashSet<string>>("trustedImages") ?? [];
        foreach (var cfg in Accounts)
        {
            _mailboxes[cfg.Id] = store.LoadMailboxes(cfg.Id).Select(m => new MailboxInfo(m.Name, m.Role)).ToList();
            foreach (var folder in Folders.Standard.Where(Folders.IsServerFolder))
                Emails.AddRange(store.LoadFolder(cfg.Id, folder));
        }
        RegisterLabels(Emails);
    }

    // MARK: State

    public List<MailAccountConfig> Accounts { get; }
    public List<Email> Emails { get; } = [];
    public string CurrentAccount { get; private set; } = "all";
    public string Folder { get; private set; } = "inbox";
    public InboxFilter Filter { get; private set; } = InboxFilter.All;
    public string? LabelFilter { get; private set; }
    public string? SearchQuery { get; private set; }
    public List<Email>? SearchResults { get; private set; }
    public HashSet<string> PinnedIds { get; }
    public Dictionary<string, DateTimeOffset> Snoozed { get; }
    public List<MailLabel> Labels { get; }
    public Dictionary<string, string> Signatures { get; }
    public HashSet<string> TrustedImageSenders { get; }
    public HashSet<string> LoadingAccounts { get; } = [];
    public string? Error { get; private set; }

    /// The message open full-width in the reader, if any.
    public Email? OpenMessage { get; private set; }
    public ComposeDraft? Compose { get; private set; }

    private readonly Dictionary<string, List<MailboxInfo>> _mailboxes = [];
    private readonly Dictionary<string, int> _pageLimits = [];
    private readonly Dictionary<string, DateTimeOffset> _localEdits = [];
    private readonly List<PendingAction> _pending = [];

    /// The list, the counts or the current folder changed.
    public event Action? Changed;
    public event Action<ToastMessage>? Toast;
    public event Action? ComposeChanged;
    public event Action? OpenMessageChanged;

    private void NotifyChanged() => Changed?.Invoke();
    public void ShowToast(string text, string? actionLabel = null, Action? action = null, TimeSpan? duration = null) =>
        Toast?.Invoke(new ToastMessage(text, actionLabel, action, duration));

    public MailAccountConfig? Config(string id) => Accounts.FirstOrDefault(a => a.Id == id);
    public bool IsAccount(string id) => Accounts.Any(a => a.Id == id);

    // MARK: The list

    public bool SearchActive => SearchResults is not null;

    public bool IsSnoozed(Email e) => Snoozed.TryGetValue(e.Id, out var until) && until > DateTimeOffset.Now;

    private IEnumerable<Email> AccountScoped =>
        CurrentAccount == "all" ? Emails : Emails.Where(e => e.Account == CurrentAccount);

    /// What the list shows, in its order: Pinned, then Unread (All tab), then the rest, newest first.
    public List<Email> VisibleEmails()
    {
        IEnumerable<Email> list;
        if (SearchResults is not null) return SearchResults;
        if (LabelFilter is { } label) list = AccountScoped.Where(e => e.Labels.Contains(label) && !IsSnoozed(e));
        else if (Folder == "snoozed") list = AccountScoped.Where(IsSnoozed);
        else if (Folder == "starred") list = AccountScoped.Where(e => e.Starred && e.Folder != "trash" && !IsSnoozed(e));
        else list = AccountScoped.Where(e => e.Folder == Folder && !IsSnoozed(e));

        if (Folder == "inbox" && LabelFilter is null)
        {
            list = Filter switch
            {
                InboxFilter.Unread => list.Where(e => e.Unread),
                InboxFilter.People => list.Where(e => !e.LooksAutomated),
                InboxFilter.Updates => list.Where(e => e.LooksAutomated),
                _ => list,
            };
        }
        return list.OrderBy(e => (int)SectionOf(e)).ThenByDescending(e => e.Date).ThenByDescending(e => e.Uid).ToList();
    }

    /// The section a row shows in, always from its current state.
    public ListSection SectionOf(Email e)
    {
        if (Folder != "inbox" || SearchActive || LabelFilter is not null) return ListSection.Rest;
        if (PinnedIds.Contains(e.Id)) return ListSection.Pinned;
        if (Filter == InboxFilter.All && e.Unread) return ListSection.Unread;
        return ListSection.Rest;
    }

    /// Titled sections; a lone section gets no title.
    public List<(string? Title, List<Email> Items)> Sections(IReadOnlyList<Email>? visible = null)
    {
        visible ??= VisibleEmails();
        var groups = visible.GroupBy(SectionOf).ToDictionary(g => g.Key, g => g.ToList());
        var pinned = groups.GetValueOrDefault(ListSection.Pinned) ?? [];
        var unread = groups.GetValueOrDefault(ListSection.Unread) ?? [];
        var rest = groups.GetValueOrDefault(ListSection.Rest) ?? [];
        var sections = new List<(string?, List<Email>)>();
        if (pinned.Count > 0) sections.Add(("Pinned", pinned));
        if (unread.Count > 0) sections.Add(("Unread", unread));
        if (sections.Count == 0) return [(null, rest)];
        if (rest.Count > 0) sections.Add(("Everything else", rest));
        return sections;
    }

    public int UnreadCount(string folder) => folder switch
    {
        "starred" => 0,
        "snoozed" => AccountScoped.Count(IsSnoozed),
        _ => AccountScoped.Count(e => e.Folder == folder && e.Unread && !IsSnoozed(e)),
    };

    public Dictionary<string, int> UnreadByAccount() =>
        Accounts.ToDictionary(a => a.Id, a => Emails.Count(e => e.Account == a.Id && e.Folder == "inbox" && e.Unread));

    // MARK: Navigation

    public void SetFolder(string folder)
    {
        Folder = Folders.Normalize(folder);
        LabelFilter = null;
        ClearSearch(notify: false);
        CloseReader(notify: false);
        NotifyChanged();
        _ = LoadCurrentAsync();
    }

    public void SetAccount(string account)
    {
        CurrentAccount = account;
        CloseReader(notify: false);
        NotifyChanged();
        _ = LoadCurrentAsync();
    }

    public void SetFilter(InboxFilter filter)
    {
        Filter = filter;
        NotifyChanged();
    }

    public void SelectLabel(string labelId)
    {
        LabelFilter = labelId;
        ClearSearch(notify: false);
        CloseReader(notify: false);
        NotifyChanged();
    }

    public void Open(Email email)
    {
        if (!Emails.Contains(email) && Emails.FirstOrDefault(e => e.Id == email.Id) is { } known) email = known;
        else if (!Emails.Contains(email)) Emails.Add(email);
        OpenMessage = email;
        OpenMessageChanged?.Invoke();
        if (email.Unread) SetRead(email, true, toast: false);
        if (!email.BodyLoaded) _ = LoadBodyAsync(email);
    }

    /// Show a message in the window, switching to its folder first (show_message).
    public void Reveal(Email email)
    {
        if (!SearchActive && email.Folder != Folder && Folders.Standard.Contains(email.Folder))
        {
            Folder = email.Folder;
            LabelFilter = null;
            NotifyChanged();
        }
        Open(email);
    }

    public void CloseReader() => CloseReader(notify: true);

    /// The reader always hears about it; <paramref name="notify"/> is about the list,
    /// which the caller refreshes itself when false.
    private void CloseReader(bool notify)
    {
        if (OpenMessage is null) return;
        OpenMessage = null;
        OpenMessageChanged?.Invoke();
        if (notify) NotifyChanged();
    }

    // MARK: Loading

    public int PageLimit(string account, string folder) => _pageLimits.GetValueOrDefault($"{account}#{folder}", 50);

    public Task LoadCurrentAsync(bool force = false)
    {
        var folder = Folder == "starred" || Folder == "snoozed" ? "inbox" : Folder;
        var accounts = CurrentAccount == "all" ? Accounts.Select(a => a.Id).ToList() : [CurrentAccount];
        return Task.WhenAll(accounts.Select(a => LoadFolderAsync(a, folder, force)));
    }

    /// Every account's inbox, plus the folder on screen.
    public async Task RefreshAsync()
    {
        WakeSnoozed();
        var tasks = Accounts.Select(a => LoadFolderAsync(a.Id, "inbox")).ToList();
        if (Folder != "inbox" && Folders.IsServerFolder(Folder)) tasks.Add(LoadCurrentAsync());
        await Task.WhenAll(tasks);
    }

    private readonly HashSet<string> _inFlight = [];

    public async Task LoadFolderAsync(string account, string folder, bool force = false)
    {
        folder = Folders.Normalize(folder);
        var cfg = Config(account);
        if (cfg is null || !Folders.IsServerFolder(folder)) return;
        var key = $"{account}#{folder}";
        if (!_inFlight.Add(key)) return;
        LoadingAccounts.Add(account);
        NotifyChanged();
        try
        {
            var box = await MailboxNameAsync(account, folder);
            if (box is null) return;
            var headers = await _mail.FetchHeadersAsync(cfg, box, PageLimit(account, folder));
            Merge(account, folder, headers, olderPage: false, complete: headers.Count < PageLimit(account, folder));
            Error = null;
        }
        catch (Exception ex)
        {
            Error = $"{cfg.Email}: {ex.Message}";
            if (force) ShowToast($"Could not refresh {cfg.Email}: {ex.Message}");
        }
        finally
        {
            _inFlight.Remove(key);
            LoadingAccounts.Remove(account);
            NotifyChanged();
        }
    }

    public async Task LoadOlderAsync()
    {
        if (!Folders.IsServerFolder(Folder)) return;
        var accounts = CurrentAccount == "all" ? Accounts.Select(a => a.Id).ToList() : [CurrentAccount];
        foreach (var account in accounts)
        {
            var cfg = Config(account)!;
            var oldest = Emails.Where(e => e.Account == account && e.Folder == Folder && e.Id.StartsWith($"{account}#{Folder}#"))
                               .Select(e => e.Uid).DefaultIfEmpty(0u).Min();
            if (oldest <= 1) continue;
            var box = await MailboxNameAsync(account, Folder);
            if (box is null) continue;
            try
            {
                var headers = await _mail.FetchHeadersAsync(cfg, box, 50, oldest);
                _pageLimits[$"{account}#{Folder}"] = PageLimit(account, Folder) + headers.Count;
                Merge(account, Folder, headers, olderPage: true);
            }
            catch (Exception ex)
            {
                ShowToast($"Could not load older mail: {ex.Message}");
            }
        }
        NotifyChanged();
    }

    /// Server truth for one folder: new messages added, flags updated, vanished ones removed.
    /// <paramref name="complete"/>: the server returned the whole mailbox, not just its newest page.
    internal void Merge(string account, string folder, IReadOnlyList<MessageHeader> headers, bool olderPage, bool complete = false)
    {
        var prefix = $"{account}#{folder}#";
        var existing = Emails.Where(e => e.Account == account && e.Folder == folder).ToList();
        var byId = existing.ToDictionary(e => e.Id);
        var changed = new List<Email>();
        var fresh = new List<Email>();
        foreach (var h in headers)
        {
            var id = Email.MakeId(account, folder, h.Uid);
            if (byId.TryGetValue(id, out var e))
            {
                if (!RecentlyEdited(id, MailFlag.Seen) && e.Unread == h.Seen) { e.Unread = !h.Seen; changed.Add(e); }
                if (!RecentlyEdited(id, MailFlag.Flagged) && e.Starred != h.Flagged) { e.Starred = h.Flagged; changed.Add(e); }
                if (e.Preview.Length == 0 && h.Preview.Length > 0) { e.Preview = h.Preview; changed.Add(e); }
            }
            else
            {
                e = MakeEmail(h, account, folder);
                fresh.Add(e);
            }
        }
        var removed = new List<Email>();
        if (!olderPage)
        {
            var onServer = headers.Select(h => Email.MakeId(account, folder, h.Uid)).ToHashSet();
            var minUid = complete || headers.Count == 0 ? 0 : headers.Min(h => h.Uid);
            foreach (var e in existing)
            {
                // Mail moved here locally is replaced by the server's copy; mail the
                // server no longer has (in the range we fetched) is gone.
                var movedHere = !e.Id.StartsWith(prefix);
                var vanished = !movedHere && e.Uid >= minUid && !onServer.Contains(e.Id);
                if ((movedHere && !IsPendingTarget(e)) || vanished) removed.Add(e);
            }
        }
        foreach (var e in removed)
        {
            Emails.Remove(e);
            if (OpenMessage == e) { OpenMessage = null; OpenMessageChanged?.Invoke(); }
        }
        Emails.AddRange(fresh);
        RegisterLabels(fresh);
        if (removed.Count > 0) _store.Delete(removed.Select(e => e.Id));
        _store.Upsert(fresh.Concat(changed.Distinct()));
    }

    private bool IsPendingTarget(Email e) => _pending.Any(p => p.EmailId == e.Id);

    internal static Email MakeEmail(MessageHeader h, string account, string folder) => new()
    {
        Id = Email.MakeId(account, folder, h.Uid), Account = account, Folder = folder, Uid = h.Uid,
        Date = h.Date, FromName = h.FromName, FromEmail = h.FromEmail, To = [.. h.To], Cc = [.. h.Cc],
        Subject = h.Subject.Length == 0 ? "(no subject)" : h.Subject, Preview = h.Preview,
        Unread = !h.Seen, Starred = h.Flagged, HasAttachment = h.HasAttachments,
        Labels = h.Keywords.Where(IsUserKeyword).ToList(),
        MessageId = h.MessageId, InReplyTo = h.InReplyTo, References = h.References,
    };

    private static bool IsUserKeyword(string k) =>
        !k.StartsWith('$') && !k.StartsWith('\\') && k is not ("junk" or "nonjunk" or "notjunk" or "forwarded");

    private bool RecentlyEdited(string id, MailFlag flag) =>
        _localEdits.TryGetValue($"{id}#{flag}", out var at) && DateTimeOffset.Now - at < TimeSpan.FromSeconds(60);

    // MARK: Mailboxes

    public IReadOnlyList<string> MailboxNames(string account) =>
        _mailboxes.GetValueOrDefault(account)?.Select(m => m.Name).Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];

    /// The server mailbox behind a standard folder, discovering the folder list if needed.
    public async Task<string?> MailboxNameAsync(string account, string folder)
    {
        folder = Folders.Normalize(folder);
        if (MailboxName(account, folder) is { } known) return known;
        var cfg = Config(account);
        if (cfg is null) return null;
        var boxes = await _mail.ListMailboxesAsync(cfg);
        _mailboxes[account] = [.. boxes];
        _store.SaveMailboxes(account, boxes.Select(b => (b.Name, b.Role)));
        return MailboxName(account, folder);
    }

    public string? MailboxName(string account, string folder)
    {
        if (!_mailboxes.TryGetValue(account, out var boxes) || boxes.Count == 0) return folder == "inbox" ? "INBOX" : null;
        string? ByRole(string role) => boxes.FirstOrDefault(b => b.Role == role)?.Name;
        return Folders.Normalize(folder) switch
        {
            "inbox" => ByRole("inbox") ?? "INBOX",
            "archive" => ByRole("archive") ?? ByRole("all"),
            var f => ByRole(f),
        };
    }

    // MARK: Bodies and attachments

    public event Action<string>? BodyLoadFailed;

    public async Task<(Email Email, ParsedMessage? Parsed)> LoadFullAsync(Email email)
    {
        var cfg = Config(email.Account);
        if (cfg is null || email.Uid == 0) return (email, null);
        var box = await MailboxNameAsync(email.Account, email.Id.Split('#')[1]) ?? "INBOX";
        var message = await _mail.FetchMessageAsync(cfg, box, email.Uid);
        var parsed = MimeTools.Parse(message);
        email.Body = parsed.Text;
        email.BodyHtml = parsed.Html;
        if (email.Preview.Length == 0) email.Preview = MimeTools.OneLine(parsed.Text);
        email.Attachments = parsed.Attachments.Select(a => new AttachmentMeta(a.FileName, a.MimeType, a.Data.LongLength)).ToList();
        email.HasAttachment = email.Attachments.Count > 0;
        email.Unsubscribe = parsed.ListUnsubscribe;
        email.CalendarEvent = parsed.Calendar;
        email.MessageId ??= message.MessageId;
        email.References ??= message.References.Count > 0 ? string.Join(' ', message.References.Select(r => $"<{r}>")) : null;
        email.BodyLoaded = true;
        email.Touch();
        foreach (var a in parsed.Attachments)
        {
            var path = AttachmentPath(email, a.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, a.Data);
        }
        _store.Upsert(email);
        return (email, parsed);
    }

    private async Task LoadBodyAsync(Email email)
    {
        try { await LoadFullAsync(email); }
        catch (Exception ex) { BodyLoadFailed?.Invoke(email.Id); ShowToast($"Could not load the message: {ex.Message}"); }
    }

    public void RetryBody(Email email) => _ = LoadBodyAsync(email);

    public string AttachmentPath(Email email, string fileName)
    {
        var safeId = string.Concat(email.Id.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        var safeName = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_store.Directory, "attachments", safeId, safeName);
    }

    /// The attachment on disk, downloading the message when needed.
    public async Task<(AttachmentMeta Meta, string Path)> AttachmentFileAsync(Email email, string? fileName, int? index)
    {
        AttachmentMeta? Pick(Email e) =>
            fileName is not null ? e.Attachments.FirstOrDefault(a => a.FileName == fileName)
            : index is { } i && i >= 0 && i < e.Attachments.Count ? e.Attachments[i]
            : e.Attachments.Count == 1 ? e.Attachments[0] : null;

        if (Pick(email) is { } meta && File.Exists(AttachmentPath(email, meta.FileName)))
            return (meta, AttachmentPath(email, meta.FileName));
        email = (await LoadFullAsync(email)).Email;
        if (Pick(email) is not { } found)
        {
            var names = string.Join(", ", email.Attachments.Select(a => a.FileName));
            throw new InvalidOperationException(names.Length == 0 ? "This message has no attachments." : $"Say which attachment: {names}.");
        }
        return (found, AttachmentPath(email, found.FileName));
    }

    // MARK: Flags

    public void SetRead(Email email, bool read, bool toast = true)
    {
        email.Unread = !read;
        PushFlag(email, MailFlag.Seen, read);
        _store.Upsert(email);
        if (toast) ShowToast(read ? "Marked as read" : "Marked as unread");
        NotifyChanged();
    }

    public void ToggleRead(Email email) => SetRead(email, email.Unread);

    public void SetStarred(Email email, bool starred)
    {
        email.Starred = starred;
        PushFlag(email, MailFlag.Flagged, starred);
        _store.Upsert(email);
        NotifyChanged();
    }

    public void ToggleStar(Email email) => SetStarred(email, !email.Starred);

    private void PushFlag(Email email, MailFlag flag, bool add)
    {
        var cfg = Config(email.Account);
        if (cfg is null || email.Uid == 0) return;
        _localEdits[$"{email.Id}#{flag}"] = DateTimeOffset.Now;
        Background(async () =>
        {
            var box = await MailboxNameAsync(email.Account, email.Id.Split('#')[1]) ?? "INBOX";
            await _mail.SetFlagAsync(cfg, box, email.Uid, flag, add);
        });
    }

    public bool IsPinned(Email e) => PinnedIds.Contains(e.Id);

    public void TogglePin(Email email)
    {
        var pin = PinnedIds.Add(email.Id);
        if (!pin) PinnedIds.Remove(email.Id);
        _store.SetPref("pinned", PinnedIds);
        ShowToast(pin ? "Pinned to the top of the inbox" : "Unpinned");
        NotifyChanged();
    }

    /// A row's main action (its left tile): mark as read when unread, else pin / unpin.
    public void PrimaryAction(Email email)
    {
        if (email.Unread) SetRead(email, true);
        else TogglePin(email);
    }

    // MARK: Triage (the server move waits for the Undo toast to expire)

    private sealed record PendingAction(string EmailId, CancellationTokenSource Cancel, Func<Task> Work);

    public void Archive(Email email) => MoveLocal(email, "archive", "Archived");
    public void Delete(Email email) => MoveLocal(email, "trash", "Deleted");
    public void MarkSpam(Email email) => MoveLocal(email, "spam", "Marked as spam");

    private void MoveLocal(Email email, string dest, string verb)
    {
        var previous = email.Folder;
        if (previous == dest) return;
        var cfg = Config(email.Account);
        var sourceFolder = email.Id.Split('#').ElementAtOrDefault(1) ?? previous;
        email.Folder = dest;
        if (OpenMessage == email) CloseReader();
        _store.Upsert(email);
        NotifyChanged();

        PendingAction? pending = null;
        if (cfg is not null && email.Uid != 0)
        {
            pending = Defer(email.Id, async () =>
            {
                var from = await MailboxNameAsync(email.Account, sourceFolder);
                var to = await MailboxNameAsync(email.Account, dest);
                if (from is null) return;
                if (to is not null) await _mail.MoveAsync(cfg, from, email.Uid, to);
                else if (dest == "trash") await _mail.SetFlagAsync(cfg, from, email.Uid, MailFlag.Deleted, true);
            });
        }
        var title = $"{verb} · {email.Subject}";
        ShowToast(title.Length > 60 ? title[..60] : title, "Undo", () =>
        {
            if (pending is not null) { pending.Cancel.Cancel(); _pending.Remove(pending); }
            email.Folder = previous;
            _store.Upsert(email);
            NotifyChanged();
        }, UndoWindow);
    }

    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(5);

    private PendingAction Defer(string emailId, Func<Task> work)
    {
        var cts = new CancellationTokenSource();
        var pending = new PendingAction(emailId, cts, work);
        _pending.Add(pending);
        _ = RunLater();
        return pending;

        async Task RunLater()
        {
            try { await Task.Delay(UndoWindow + TimeSpan.FromMilliseconds(300), cts.Token); }
            catch (TaskCanceledException) { return; }
            _pending.Remove(pending);
            await RunReporting(work);
        }
    }

    /// Runs the server actions still waiting on an Undo (when the app closes).
    public async Task FlushPendingAsync()
    {
        var all = _pending.ToList();
        _pending.Clear();
        foreach (var p in all) { p.Cancel.Cancel(); await RunReporting(p.Work); }
    }

    private void Background(Func<Task> work) => _ = RunReporting(work);

    private async Task RunReporting(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { ShowToast($"Server error: {ex.Message}"); }
    }

    public void MoveToMailbox(Email email, string mailbox)
    {
        var cfg = Config(email.Account) ?? throw new InvalidOperationException("Unknown account.");
        if (!MailboxNames(email.Account).Contains(mailbox)) throw new InvalidOperationException($"No mailbox named {mailbox}.");
        var sourceFolder = email.Id.Split('#')[1];
        Emails.Remove(email);
        _store.Delete([email.Id]);
        if (OpenMessage == email) CloseReader();
        NotifyChanged();
        Background(async () =>
        {
            var from = await MailboxNameAsync(email.Account, sourceFolder) ?? "INBOX";
            await _mail.MoveAsync(cfg, from, email.Uid, mailbox);
        });
        ShowToast($"Moved to {mailbox}");
    }

    public void Snooze(Email email, DateTimeOffset until, string label)
    {
        Snoozed[email.Id] = until;
        _store.SetPref("snoozed", Snoozed);
        if (OpenMessage == email) CloseReader();
        NotifyChanged();
        ShowToast($"Snoozed {label}", "Undo", () =>
        {
            Snoozed.Remove(email.Id);
            _store.SetPref("snoozed", Snoozed);
            NotifyChanged();
        });
    }

    public static IReadOnlyList<(string Label, DateTimeOffset When)> SnoozePresets(DateTimeOffset now)
    {
        var list = new List<(string, DateTimeOffset)> { ("Later today", now.AddHours(3)) };
        var evening = new DateTimeOffset(now.Date.AddHours(18), now.Offset);
        if (evening > now.AddHours(1)) list.Add(("This evening", evening));
        list.Add(("Tomorrow", new DateTimeOffset(now.Date.AddDays(1).AddHours(8), now.Offset)));
        var toSaturday = ((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7;
        list.Add(("This weekend", new DateTimeOffset(now.Date.AddDays(toSaturday == 0 ? 7 : toSaturday).AddHours(9), now.Offset)));
        var toMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        list.Add(("Next week", new DateTimeOffset(now.Date.AddDays(toMonday == 0 ? 7 : toMonday).AddHours(8), now.Offset)));
        return list;
    }

    public void WakeSnoozed()
    {
        var due = Snoozed.Where(s => s.Value <= DateTimeOffset.Now).Select(s => s.Key).ToList();
        if (due.Count == 0) return;
        foreach (var id in due) Snoozed.Remove(id);
        _store.SetPref("snoozed", Snoozed);
        NotifyChanged();
    }

    // MARK: Labels (IMAP keywords)

    public static string SanitizeLabelId(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        return string.Concat(chars).Trim('-');
    }

    public string AddLabel(string name)
    {
        var id = SanitizeLabelId(name);
        if (id.Length == 0) throw new InvalidOperationException("Empty label name.");
        if (Labels.All(l => l.Id != id))
        {
            Labels.Add(new MailLabel(id, name.Trim(), Sender.StableColorHex(id)));
            _store.SetPref("labels", Labels);
        }
        return id;
    }

    public void ApplyLabel(Email email, string labelId, bool add)
    {
        if (add && !email.Labels.Contains(labelId)) email.Labels.Add(labelId);
        if (!add) email.Labels.Remove(labelId);
        email.Touch(nameof(Email.Labels));
        _store.Upsert(email);
        NotifyChanged();
        var cfg = Config(email.Account);
        if (cfg is null || email.Uid == 0) return;
        Background(async () =>
        {
            var box = await MailboxNameAsync(email.Account, email.Id.Split('#')[1]) ?? "INBOX";
            await _mail.SetKeywordAsync(cfg, box, email.Uid, labelId, add);
        });
    }

    private void RegisterLabels(IEnumerable<Email> list)
    {
        var added = false;
        foreach (var id in list.SelectMany(e => e.Labels).Distinct())
        {
            if (Labels.Any(l => l.Id == id)) continue;
            Labels.Add(new MailLabel(id, id.Replace('-', ' '), Sender.StableColorHex(id)));
            added = true;
        }
        if (added) _store.SetPref("labels", Labels);
    }

    // MARK: Search

    public async Task SearchAsync(string query, bool includeServer = true)
    {
        query = query.Trim();
        if (query.Length == 0) { ClearSearch(); return; }
        SearchQuery = query;
        var accounts = CurrentAccount == "all" ? Accounts.Select(a => a.Id).ToList() : [CurrentAccount];
        SearchResults = Canonical(LocalSearch(query, accounts));
        CloseReader(notify: false);
        NotifyChanged();
        if (!includeServer) return;
        var hits = await ServerSearchAsync(query, accounts, Folder, 50);
        if (SearchQuery != query) return;
        foreach (var e in hits)
            if (SearchResults!.All(r => r.Id != e.Id)) SearchResults.Add(e);
        SearchResults = SearchResults!.OrderByDescending(e => e.Date).ToList();
        NotifyChanged();
    }

    public void ClearSearch() => ClearSearch(notify: true);

    private void ClearSearch(bool notify)
    {
        if (SearchResults is null && SearchQuery is null) return;
        SearchResults = null;
        SearchQuery = null;
        if (notify) NotifyChanged();
    }

    public List<Email> LocalSearch(string query, IReadOnlyCollection<string> accounts, int limit = 50)
    {
        var q = query.ToLowerInvariant();
        var byId = _store.Search(query, accounts, limit).ToDictionary(e => e.Id);
        foreach (var e in Emails.Where(e => accounts.Contains(e.Account)))
        {
            if (e.Subject.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Preview.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Body.Contains(q, StringComparison.OrdinalIgnoreCase) || e.SenderName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.FromEmail.Contains(q, StringComparison.OrdinalIgnoreCase) || e.To.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)))
                byId[e.Id] = e;
        }
        return byId.Values.OrderByDescending(e => e.Date).Take(limit).ToList();
    }

    public async Task<List<Email>> ServerSearchAsync(string query, IReadOnlyCollection<string> accounts, string folder, int limit)
    {
        var results = new List<Email>();
        var serverFolder = Folders.IsServerFolder(folder) ? folder : "inbox";
        foreach (var account in accounts)
        {
            var cfg = Config(account);
            if (cfg is null) continue;
            try
            {
                var box = await MailboxNameAsync(account, serverFolder) ?? "INBOX";
                var hits = await _mail.SearchAsync(cfg, box, query, limit);
                results.AddRange(hits.Select(h => MakeEmail(h, account, serverFolder)));
            }
            catch (Exception) { /* offline: the local results stand */ }
        }
        return Canonical(results);
    }

    /// The same message object the list holds, when it has it.
    private List<Email> Canonical(IEnumerable<Email> list) =>
        list.Select(e => Emails.FirstOrDefault(x => x.Id == e.Id) ?? e).ToList();

    public Email? Find(string id) =>
        Emails.FirstOrDefault(e => e.Id == id) ?? SearchResults?.FirstOrDefault(e => e.Id == id) ?? _store.Get(id);

    public List<Contact> Contacts(int limit) => _store.Contacts(limit);

    // MARK: Compose

    public string Signature(string accountId) => Signatures.GetValueOrDefault(accountId, "");

    public void SetSignature(string accountId, string text)
    {
        Signatures[accountId] = text;
        _store.SetPref("signatures", Signatures);
    }

    public ComposeDraft StartCompose(string to = "", string subject = "", string body = "", string? fromId = null,
                                     string title = "New message")
    {
        var from = fromId ?? (CurrentAccount != "all" ? CurrentAccount : Accounts.FirstOrDefault()?.Id ?? "");
        var signature = Signature(from);
        var draft = new ComposeDraft
        {
            To = to, Subject = subject, FromId = from, Title = title,
            Body = body + (signature.Length == 0 ? "" : $"\n\n-- \n{signature}"),
        };
        SetCompose(draft);
        return draft;
    }

    public ComposeDraft Reply(Email original, bool all)
    {
        var to = new List<string> { original.FromEmail };
        if (all)
        {
            var me = Config(original.Account)?.Email ?? "";
            to.AddRange(MimeTools.ParseRecipients(string.Join(", ", original.To.Concat(original.Cc)))
                                 .Where(a => !a.Equals(me, StringComparison.OrdinalIgnoreCase)));
        }
        var subject = original.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? original.Subject : $"Re: {original.Subject}";
        var draft = StartCompose(string.Join(", ", to.Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)),
                                 subject, "", original.Account, $"Reply · {original.SenderName}");
        draft.Body += MimeTools.Quote(original);
        draft.InReplyTo = original.MessageId;
        draft.References = string.Join(' ', new[] { original.References, original.MessageId is { } m ? $"<{m.Trim('<', '>')}>" : null }
                                                .Where(s => !string.IsNullOrEmpty(s)));
        return draft;
    }

    public async Task<ComposeDraft> ForwardAsync(Email original)
    {
        var (full, parsed) = original.BodyLoaded && original.Attachments.Count == 0 ? (original, null) : await LoadFullAsync(original);
        var subject = full.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ? full.Subject : $"Fwd: {full.Subject}";
        var draft = StartCompose("", subject, "", full.Account, "Forward");
        draft.Body += $"\n\n--- Forwarded message ---\nFrom: {full.FromDisplay}\nSubject: {full.Subject}\n\n{full.Body}";
        foreach (var a in parsed?.Attachments ?? []) draft.Attachments.Add(a);
        return draft;
    }

    public void EditDraft(Email draftEmail)
    {
        var draft = StartCompose(string.Join(", ", draftEmail.To), draftEmail.Subject, draftEmail.Body, draftEmail.Account, "Draft");
        draft.Body = draftEmail.Body;
        draft.OriginalDraftId = draftEmail.Id;
    }

    public void SetCompose(ComposeDraft? draft)
    {
        Compose = draft;
        ComposeChanged?.Invoke();
    }

    public void CloseCompose() => SetCompose(null);

    /// Save to the Drafts folder (best effort) and close the composer.
    public void SaveDraftAndClose(ComposeDraft draft)
    {
        SetCompose(null);
        var cfg = Config(draft.FromId);
        if (draft.IsBlank || cfg is null) return;
        var message = MimeTools.Build(cfg, null, draft);
        Background(async () =>
        {
            if (await MailboxNameAsync(cfg.Id, "drafts") is not { } box) return;
            await _mail.AppendAsync(cfg, box, message, seen: true, draft: true);
            if (draft.OriginalDraftId is { } old && Find(old) is { } previous && previous.Uid != 0)
            {
                await _mail.SetFlagAsync(cfg, box, previous.Uid, MailFlag.Deleted, true);
                Emails.Remove(previous);
                _store.Delete([previous.Id]);
            }
            ShowToast("Draft saved");
            await LoadFolderAsync(cfg.Id, "drafts");
        });
    }

    /// Close the composer and send after the Undo window.
    public void SendDraft(ComposeDraft draft)
    {
        var cfg = Config(draft.FromId) ?? throw new InvalidOperationException("Choose the account to send from.");
        if (MimeTools.ParseRecipients($"{draft.To},{draft.Cc},{draft.Bcc}").Count == 0)
            throw new InvalidOperationException("The message has no recipient.");
        SetCompose(null);
        var cts = new CancellationTokenSource();
        ShowToast($"Sending to {draft.To}…", "Undo", () => { cts.Cancel(); SetCompose(draft); }, UndoWindow);
        _ = SendLater();

        async Task SendLater()
        {
            try { await Task.Delay(UndoWindow + TimeSpan.FromMilliseconds(300), cts.Token); }
            catch (TaskCanceledException) { return; }
            try
            {
                var message = MimeTools.Build(cfg, null, draft);
                await _mail.SendAsync(cfg, message);
                // Gmail files sent mail itself; other servers need the copy.
                if (!cfg.IsGmail && await MailboxNameAsync(cfg.Id, "sent") is { } sent)
                    await _mail.AppendAsync(cfg, sent, message, seen: true, draft: false);
                if (draft.OriginalDraftId is { } old && Find(old) is { } previous)
                {
                    Emails.Remove(previous);
                    _store.Delete([previous.Id]);
                    if (await MailboxNameAsync(cfg.Id, "drafts") is { } drafts && previous.Uid != 0)
                        await _mail.SetFlagAsync(cfg, drafts, previous.Uid, MailFlag.Deleted, true);
                }
                ShowToast("Message sent");
                NotifyChanged();
            }
            catch (Exception ex)
            {
                ShowToast($"Not sent: {ex.Message}", "Edit", () => SetCompose(draft), TimeSpan.FromSeconds(10));
            }
        }
    }

    // MARK: Accounts

    public async Task<MailAccountConfig> AddAccountAsync(MailAccountConfig cfg, string imapPassword, string? smtpPassword = null)
    {
        smtpPassword ??= imapPassword;
        await _mail.VerifyAsync(cfg, imapPassword, smtpPassword);
        _credentials.Set(cfg.ImapPasswordKey, imapPassword);
        _credentials.Set(cfg.SmtpPasswordKey, smtpPassword);
        Accounts.RemoveAll(a => a.Id == cfg.Id);
        Accounts.Add(cfg);
        _store.SaveAccount(cfg, Accounts.Count - 1);
        NotifyChanged();
        await LoadFolderAsync(cfg.Id, "inbox", force: true);
        return cfg;
    }

    public void RemoveAccount(string id)
    {
        if (Config(id) is not { } cfg) return;
        Accounts.Remove(cfg);
        Emails.RemoveAll(e => e.Account == id);
        _store.DeleteAccount(id);
        _credentials.Delete(cfg.ImapPasswordKey);
        _credentials.Delete(cfg.SmtpPasswordKey);
        _mail.Forget(id);
        if (CurrentAccount == id) CurrentAccount = "all";
        NotifyChanged();
    }

    // MARK: Remote images

    public bool ImagesTrusted(Email e) => TrustedImageSenders.Contains(e.FromEmail.ToLowerInvariant());

    public void TrustImages(Email e)
    {
        TrustedImageSenders.Add(e.FromEmail.ToLowerInvariant());
        _store.SetPref("trustedImages", TrustedImageSenders);
        e.Touch(nameof(Email.BodyHtml));
    }
}
