using System.Text.Json.Nodes;
using MMail.Core.Mail;
using MMail.Core.Models;
using MMail.Core.State;

namespace MMail.Core.Agent;

/// The MCP tools Claude sees. Each one drives <see cref="MailController"/>, the same
/// object the window uses, so every action is visible on screen (toasts, Undo, composer).
/// Same 22 tools as the macOS app.
public static class AgentTools
{
    public static readonly string[] FolderIds = ["inbox", "starred", "snoozed", "done", "archive", "sent", "drafts", "spam", "trash"];

    public const string Instructions =
        "MMail is the user's own mail app, running on their Windows PC. Message ids come from " +
        "list_messages / search_messages. To write mail: compose_message opens a composer on " +
        "screen that the user can see and edit; adjust it with update_compose and add_attachment, " +
        "then save_draft or send_message. Never send unless the user explicitly asked you to; " +
        "when in doubt, leave the composer open or save a draft. Archive, delete and spam show an " +
        "Undo toast in the app.";

    public static McpServer CreateServer(MailController model)
    {
        var server = new McpServer("mmail", "1.0.0", Instructions);
        new Handlers(model).Register(server);
        return server;
    }

    private sealed class Handlers(MailController model)
    {
        /// Messages seen through search that are not in the app's loaded folders.
        private readonly Dictionary<string, Email> _known = [];

        private static JsonObject Props(params (string Key, JsonObject Schema)[] items)
        {
            var o = new JsonObject();
            foreach (var (k, s) in items) o[k] = s;
            return o;
        }

        private static Task<JsonArray> Done(params JsonObject[] content) => Task.FromResult(new JsonArray(content.Cast<JsonNode>().ToArray()));
        private static JsonArray Out(params JsonObject[] content) => new(content.Cast<JsonNode>().ToArray());

        public void Register(McpServer server)
        {
            static JsonObject FolderSchema() => Schema.OneOf(FolderIds, "Folder (default: inbox).");

            // MARK: Reading

            server.Register("list_accounts", "List the connected mail accounts with their unread counts.", null, null, _ =>
            {
                var unread = model.UnreadByAccount();
                var list = new JsonArray(model.Accounts.Select(a => (JsonNode)new JsonObject
                {
                    ["id"] = a.Id, ["name"] = a.DisplayName, ["email"] = a.Email, ["unread"] = unread.GetValueOrDefault(a.Id),
                }).ToArray());
                return Done(Content.Json(list));
            });

            server.Register("list_folders", "List the folders of an account: standard folders plus every server mailbox (usable with move_message).",
                Props(("account", Schema.String("Account id (from list_accounts)."))), ["account"], async args =>
                {
                    var account = args.RequireString("account");
                    if (!model.IsAccount(account)) throw new ToolError($"Unknown account {account}.");
                    await model.MailboxNameAsync(account, "inbox");
                    if (model.MailboxNames(account).Count <= 1) await model.MailboxNameAsync(account, "sent");
                    return Out(Content.Json(new JsonObject
                    {
                        ["standard"] = new JsonArray(FolderIds.Select(f => (JsonNode)f).ToArray()),
                        ["server_mailboxes"] = new JsonArray(model.MailboxNames(account).Select(n => (JsonNode)n).ToArray()),
                    }));
                });

            server.Register("list_messages", "List messages in a folder, newest first.",
                Props(("folder", FolderSchema()),
                      ("account", Schema.String("Limit to one account (default: all accounts).")),
                      ("unread_only", Schema.Boolean("Only unread messages.")),
                      ("label", Schema.String("Only messages with this label id.")),
                      ("limit", Schema.Integer("Maximum number of messages (default 30).")),
                      ("refresh", Schema.Boolean("Fetch new mail from the server first."))), null, async args =>
                {
                    var folder = Folders.Normalize(args.Str("folder") ?? "inbox");
                    var accounts = Scope(args.Str("account"));
                    await Hydrate(folder, accounts, args.Bool("refresh") ?? false);
                    IEnumerable<Email> list = Messages(folder, accounts);
                    if (args.Bool("unread_only") == true) list = list.Where(e => e.Unread);
                    if (args.Str("label") is { } label) list = list.Where(e => e.Labels.Contains(label));
                    return Out(Content.Json(new JsonArray(list.Take(args.Int("limit") ?? 30).Select(e => (JsonNode)Summary(e)).ToArray())));
                });

            server.Register("search_messages", "Search mail by sender, subject or body, in the local cache and on the server.",
                Props(("query", Schema.String("Words to look for.")), ("folder", FolderSchema()),
                      ("account", Schema.String("Limit to one account.")),
                      ("limit", Schema.Integer("Maximum number of results (default 30)."))), ["query"], async args =>
                {
                    var query = args.RequireString("query");
                    var accounts = Scope(args.Str("account"));
                    var limit = args.Int("limit") ?? 30;
                    var results = model.LocalSearch(query, accounts, limit);
                    var server = await model.ServerSearchAsync(query, accounts, Folders.Normalize(args.Str("folder") ?? "inbox"), limit);
                    foreach (var e in server.Where(e => results.All(r => r.Id != e.Id))) results.Add(e);
                    foreach (var e in results) _known.TryAdd(e.Id, e);
                    return Out(Content.Json(new JsonArray(results.OrderByDescending(e => e.Date).Take(limit).Select(e => (JsonNode)Summary(e)).ToArray())));
                });

            server.Register("get_message", "Read a full message: headers, plain-text body (HTML on request) and attachment list.",
                Props(("id", Schema.String("Message id.")), ("include_html", Schema.Boolean("Also return the HTML body.")),
                      ("mark_read", Schema.Boolean("Mark as read (default false)."))), ["id"], async args =>
                {
                    var email = Require(args.RequireString("id"));
                    if (!email.BodyLoaded) email = (await model.LoadFullAsync(email)).Email;
                    if (args.Bool("mark_read") == true) model.SetRead(InModel(email), true, toast: false);
                    var o = Summary(email);
                    o["body"] = email.Body;
                    if (args.Bool("include_html") == true && email.BodyHtml is { } html) o["html"] = html;
                    o["attachments"] = new JsonArray(email.Attachments.Select((a, i) => (JsonNode)new JsonObject
                    {
                        ["index"] = i, ["filename"] = a.FileName, ["mime_type"] = a.MimeType, ["size"] = a.Size,
                    }).ToArray());
                    if (email.CalendarEvent is { } ev)
                        o["calendar_event"] = new JsonObject { ["summary"] = ev.Summary, ["location"] = ev.Location ?? "", ["start"] = ev.Start?.ToString("o") };
                    if (email.Unsubscribe is not null) o["has_unsubscribe"] = true;
                    return Out(Content.Json(o));
                });

            server.Register("get_attachment",
                "Open an attachment. Text comes back as text, images as images, anything else as a binary resource; the local file path is always given.",
                Props(("id", Schema.String("Message id.")), ("filename", Schema.String("Attachment file name (or use index).")),
                      ("index", Schema.Integer("Attachment index from get_message."))), ["id"], async args =>
                {
                    var (meta, path) = await model.AttachmentFileAsync(Require(args.RequireString("id")), args.Str("filename"), args.Int("index"));
                    var data = await File.ReadAllBytesAsync(path);
                    var info = Content.Text($"{meta.FileName} — {meta.MimeType}, {data.Length} bytes — {path}");
                    if (data.Length > 8 * 1024 * 1024) return Out(info, Content.Text("Too large to return inline; read the file at the path above."));
                    if (MimeTools.IsText(meta.MimeType)) return Out(info, Content.Text(System.Text.Encoding.UTF8.GetString(data)));
                    if (meta.MimeType.StartsWith("image/"))
                        return Out(info, new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = meta.MimeType });
                    return Out(info, new JsonObject
                    {
                        ["type"] = "resource",
                        ["resource"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri, ["mimeType"] = meta.MimeType, ["blob"] = Convert.ToBase64String(data) },
                    });
                });

            server.Register("save_attachment", "Save an attachment to a file or folder on the PC.",
                Props(("id", Schema.String("Message id.")), ("filename", Schema.String("Attachment file name (or use index).")),
                      ("index", Schema.Integer("Attachment index from get_message.")),
                      ("destination", Schema.String("Destination folder or file path (~ allowed)."))), ["id", "destination"], async args =>
                {
                    var (meta, path) = await model.AttachmentFileAsync(Require(args.RequireString("id")), args.Str("filename"), args.Int("index"));
                    var dest = ExpandHome(args.RequireString("destination"));
                    if (Directory.Exists(dest)) dest = Path.Combine(dest, meta.FileName);
                    File.Copy(path, dest, overwrite: false);
                    return Out(Content.Text($"Saved to {dest}"));
                });

            server.Register("list_contacts", "People the user receives mail from, most recent first.",
                Props(("limit", Schema.Integer("Maximum (default 50)."))), null, args =>
                    Done(Content.Json(new JsonArray(model.Contacts(args.Int("limit") ?? 50)
                        .Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["email"] = c.Email }).ToArray()))));

            server.Register("list_labels", "List the user's labels.", null, null, _ =>
                Done(Content.Json(new JsonArray(model.Labels.Select(l => (JsonNode)new JsonObject { ["id"] = l.Id, ["name"] = l.Name }).ToArray()))));

            server.Register("show_message", "Open a message in the app window so the user sees it.",
                Props(("id", Schema.String("Message id."))), ["id"], args =>
                {
                    model.Reveal(Require(args.RequireString("id")));
                    return Done(Content.Text("Shown in MMail."));
                });

            // MARK: Organizing

            server.Register("set_flags", "Mark a message read/unread and/or starred.",
                Props(("id", Schema.String("Message id.")), ("read", Schema.Boolean("Read.")), ("starred", Schema.Boolean("Starred."))),
                ["id"], args =>
                {
                    var email = InModel(Require(args.RequireString("id")));
                    if (args.Bool("read") is { } read) model.SetRead(email, read, toast: false);
                    if (args.Bool("starred") is { } starred) model.SetStarred(email, starred);
                    return Done(Content.Text("OK"));
                });

            server.Register("triage", "Archive, mark done, delete (to Trash) or mark a message as spam. The app shows an Undo toast.",
                Props(("id", Schema.String("Message id.")), ("action", Schema.OneOf(["archive", "done", "delete", "spam"], "What to do."))),
                ["id", "action"], args =>
                {
                    var email = InModel(Require(args.RequireString("id")));
                    switch (args.RequireString("action"))
                    {
                        case "archive" or "done": model.Archive(email); break;
                        case "delete": model.Delete(email); break;
                        case "spam": model.MarkSpam(email); break;
                        default: throw new ToolError("Unknown action.");
                    }
                    return Done(Content.Text("Done."));
                });

            server.Register("move_message", "Move a message to any server mailbox (see list_folders).",
                Props(("id", Schema.String("Message id.")), ("mailbox", Schema.String("Server mailbox name."))), ["id", "mailbox"], async args =>
                {
                    var email = InModel(Require(args.RequireString("id")));
                    var mailbox = args.RequireString("mailbox");
                    if (!model.MailboxNames(email.Account).Contains(mailbox)) await model.MailboxNameAsync(email.Account, "sent");
                    model.MoveToMailbox(email, mailbox);
                    return Out(Content.Text("Moved."));
                });

            server.Register("snooze_message", "Hide a message until a given time.",
                Props(("id", Schema.String("Message id.")), ("until", Schema.String("ISO 8601 date-time, e.g. 2026-10-21T08:00:00+02:00."))),
                ["id", "until"], args =>
                {
                    var email = InModel(Require(args.RequireString("id")));
                    if (!DateTimeOffset.TryParse(args.RequireString("until"), System.Globalization.CultureInfo.InvariantCulture,
                                                 System.Globalization.DateTimeStyles.AssumeLocal, out var until))
                        throw new ToolError("“until” must be an ISO 8601 date-time.");
                    model.Snooze(email, until, $"until {until.LocalDateTime:g}");
                    return Done(Content.Text("Snoozed."));
                });

            server.Register("label_message", "Add or remove a label on a message (created if needed).",
                Props(("id", Schema.String("Message id.")), ("label", Schema.String("Label name or id.")),
                      ("remove", Schema.Boolean("Remove instead of add."))), ["id", "label"], args =>
                {
                    var email = InModel(Require(args.RequireString("id")));
                    var name = args.RequireString("label");
                    var remove = args.Bool("remove") ?? false;
                    var labelId = remove ? MailController.SanitizeLabelId(name) : model.AddLabel(name);
                    model.ApplyLabel(email, labelId, !remove);
                    return Done(Content.Text(remove ? "Label removed." : $"Label “{labelId}” applied."));
                });

            // MARK: Writing

            server.Register("compose_message",
                "Open a composer in the app, pre-filled. Optionally as a reply (reply_to_id) or a forward (forward_id, original attachments included). The user sees it on screen.",
                Props(("to", Schema.Strings("Recipients.")), ("cc", Schema.Strings("Cc.")), ("bcc", Schema.Strings("Bcc.")),
                      ("subject", Schema.String("Subject (derived automatically for replies and forwards).")),
                      ("body", Schema.String("Plain-text body you write; the signature and any quote are added after it.")),
                      ("from_account", Schema.String("Sending account id (default: the message's account, or the first one).")),
                      ("reply_to_id", Schema.String("Message to reply to.")), ("reply_all", Schema.Boolean("Reply to everyone.")),
                      ("forward_id", Schema.String("Message to forward.")),
                      ("replace_open", Schema.Boolean("Replace a composer the user already has open."))), null, async args =>
                {
                    if (model.Compose is { IsBlank: false } && args.Bool("replace_open") != true)
                        throw new ToolError("The user already has a composer open. Read it with get_compose, or pass replace_open: true.");
                    var body = args.RawStr("body") ?? "";
                    ComposeDraft draft;
                    if (args.Str("reply_to_id") is { } replyId)
                    {
                        var original = Require(replyId);
                        if (!original.BodyLoaded) original = (await model.LoadFullAsync(original)).Email;
                        draft = model.Reply(original, args.Bool("reply_all") == true);
                        draft.Title = $"Claude · Reply · {original.SenderName}";
                    }
                    else if (args.Str("forward_id") is { } forwardId)
                    {
                        draft = await model.ForwardAsync(Require(forwardId));
                        draft.Title = "Claude · Forward";
                    }
                    else
                    {
                        draft = model.StartCompose(fromId: args.Str("from_account"), title: "Claude · New message");
                    }
                    // The body Claude writes goes before the signature and any quote.
                    draft.Body = body + draft.Body;
                    if (args.Recipients("to") is { Length: > 0 } to) draft.To = to;
                    if (args.Recipients("cc") is { } cc) draft.Cc = cc;
                    if (args.Recipients("bcc") is { } bcc) draft.Bcc = bcc;
                    if (args.Str("subject") is { } subject) draft.Subject = subject;
                    if (args.Str("from_account") is { } from) draft.FromId = from;
                    return Out(Content.Json(Describe(draft)));
                });

            server.Register("get_compose", "Read the composer currently open in the app, including the user's own edits.", null, null, _ =>
                Done(model.Compose is { } draft ? Content.Json(Describe(draft)) : Content.Text("No composer is open.")));

            server.Register("update_compose", "Change fields of the open composer; only the fields given change.",
                Props(("to", Schema.Strings("Recipients.")), ("cc", Schema.Strings("Cc.")), ("bcc", Schema.Strings("Bcc.")),
                      ("subject", Schema.String("Subject.")), ("body", Schema.String("Replace the whole body.")),
                      ("append_body", Schema.String("Text appended at the end of the body.")),
                      ("from_account", Schema.String("Sending account id."))), null, args =>
                {
                    var draft = RequireCompose();
                    if (args.Recipients("to") is { } to) draft.To = to;
                    if (args.Recipients("cc") is { } cc) draft.Cc = cc;
                    if (args.Recipients("bcc") is { } bcc) draft.Bcc = bcc;
                    if (args.RawStr("subject") is { } subject) draft.Subject = subject;
                    if (args.RawStr("body") is { } body) draft.Body = body;
                    if (args.RawStr("append_body") is { } more) draft.Body += more;
                    if (args.Str("from_account") is { } from)
                    {
                        if (!model.IsAccount(from)) throw new ToolError($"Unknown account {from}.");
                        draft.FromId = from;
                    }
                    return Done(Content.Json(Describe(draft)));
                });

            server.Register("add_attachment",
                "Attach a file to the open composer: a local file (file_path), text (text + filename) or bytes (content_base64 + filename).",
                Props(("file_path", Schema.String("File on the PC (~ allowed).")),
                      ("filename", Schema.String("Attachment name (required with text or content_base64).")),
                      ("text", Schema.String("Text content to attach.")), ("content_base64", Schema.String("Binary content, base64.")),
                      ("mime_type", Schema.String("MIME type (guessed from the extension otherwise)."))), null, async args =>
                {
                    var draft = RequireCompose();
                    byte[] data;
                    string fileName;
                    if (args.Str("file_path") is { } path)
                    {
                        path = ExpandHome(path);
                        data = await File.ReadAllBytesAsync(path);
                        fileName = args.Str("filename") ?? Path.GetFileName(path);
                    }
                    else if (args.RawStr("text") is { } text)
                    {
                        data = System.Text.Encoding.UTF8.GetBytes(text);
                        fileName = args.RequireString("filename");
                    }
                    else if (args.Str("content_base64") is { } b64)
                    {
                        try { data = Convert.FromBase64String(b64.Trim()); }
                        catch (FormatException) { throw new ToolError("Invalid base64."); }
                        fileName = args.RequireString("filename");
                    }
                    else throw new ToolError("Give file_path, text or content_base64.");
                    draft.Attachments.Add(new ComposeAttachment(fileName, args.Str("mime_type") ?? MimeTools.GuessMime(fileName), data));
                    return Out(Content.Json(Describe(draft)));
                });

            server.Register("remove_attachment", "Remove an attachment from the open composer.",
                Props(("filename", Schema.String("Attachment file name."))), ["filename"], args =>
                {
                    var draft = RequireCompose();
                    var name = args.RequireString("filename");
                    var found = draft.Attachments.Where(a => a.FileName == name).ToList();
                    if (found.Count == 0) throw new ToolError($"No attachment named {name}.");
                    foreach (var a in found) draft.Attachments.Remove(a);
                    return Done(Content.Json(Describe(draft)));
                });

            server.Register("save_draft", "Save the open composer to the Drafts folder and close it.", null, null, _ =>
            {
                model.SaveDraftAndClose(RequireCompose());
                return Done(Content.Text("Draft saved."));
            });

            server.Register("send_message",
                "Send the open composer. Only when the user explicitly asked to send. The app keeps a 5-second Undo before it goes out.",
                Props(("confirm", Schema.Boolean("Must be true: the user asked for this message to be sent."))), ["confirm"], args =>
                {
                    if (args.Bool("confirm") != true) throw new ToolError("Set confirm: true once the user has asked to send.");
                    var draft = RequireCompose();
                    model.SendDraft(draft);
                    return Done(Content.Text($"Sending to {draft.To}. The user can still undo it for 5 seconds in the app."));
                });
        }

        // MARK: Lookup

        private List<string> Scope(string? account)
        {
            if (account is null) return model.Accounts.Select(a => a.Id).ToList();
            if (!model.IsAccount(account)) throw new ToolError($"Unknown account {account}.");
            return [account];
        }

        private Email Require(string id) =>
            model.Find(id) ?? _known.GetValueOrDefault(id)
            ?? throw new ToolError($"Message {id} not found. Use list_messages or search_messages first.");

        /// Triage actions work on the app's loaded mail; bring search hits in first.
        private Email InModel(Email e)
        {
            if (model.Emails.FirstOrDefault(x => x.Id == e.Id) is { } loaded) return loaded;
            model.Emails.Add(e);
            return e;
        }

        private List<Email> Messages(string folder, IReadOnlyCollection<string> accounts) =>
            model.Emails.Where(e => accounts.Contains(e.Account) && folder switch
                {
                    "starred" => e.Starred && e.Folder != "trash",
                    "snoozed" => model.IsSnoozed(e),
                    _ => e.Folder == folder && !model.IsSnoozed(e),
                })
                .OrderByDescending(e => e.Date).ToList();

        /// Make sure a folder is in memory before listing it.
        private async Task Hydrate(string folder, IReadOnlyCollection<string> accounts, bool force)
        {
            var server = Folders.IsServerFolder(folder) ? folder : "inbox";
            var needed = accounts.Where(a => force || !model.Emails.Any(e => e.Account == a && e.Folder == server));
            var load = Task.WhenAll(needed.Select(a => model.LoadFolderAsync(a, server, force)));
            await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(30)));
        }

        // MARK: Composer

        private ComposeDraft RequireCompose() =>
            model.Compose ?? throw new ToolError("No composer is open. Use compose_message first.");

        private JsonObject Describe(ComposeDraft d) => new()
        {
            ["from"] = model.Config(d.FromId)?.Email ?? d.FromId,
            ["to"] = d.To, ["cc"] = d.Cc, ["bcc"] = d.Bcc, ["subject"] = d.Subject, ["body"] = d.Body,
            ["attachments"] = new JsonArray(d.Attachments.Select(a => (JsonNode)new JsonObject
            {
                ["filename"] = a.FileName, ["mime_type"] = a.MimeType, ["size"] = a.Data.Length,
            }).ToArray()),
        };

        private JsonObject Summary(Email e)
        {
            var o = new JsonObject
            {
                ["id"] = e.Id, ["account"] = e.Account, ["folder"] = e.Folder,
                ["from"] = e.Folder is "sent" or "drafts" ? model.Config(e.Account)?.Email ?? "me" : e.FromDisplay,
                ["to"] = new JsonArray(e.To.Select(t => (JsonNode)t).ToArray()),
                ["subject"] = e.Subject, ["date"] = e.Date.ToString("o"), ["unread"] = e.Unread, ["starred"] = e.Starred,
                ["pinned"] = model.IsPinned(e), ["has_attachments"] = e.HasAttachment || e.Attachments.Count > 0,
                ["labels"] = new JsonArray(e.Labels.Select(l => (JsonNode)l).ToArray()),
            };
            if (e.Preview.Length > 0) o["preview"] = e.Preview;
            return o;
        }

        private static string ExpandHome(string path) =>
            path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path;
    }
}
