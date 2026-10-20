import Foundation

/// The MCP tools Claude sees. Each one drives `AppModel`, the same object the
/// window uses, so every action is visible on screen (toasts, Undo, composer).
@MainActor
enum AgentTools {
    static let folderIds = ["inbox", "starred", "snoozed", "done", "archive", "sent", "drafts", "spam", "trash"]

    static func makeServer(model: AppModel) -> MCPServer {
        let server = MCPServer(name: "mmail", version: "1.0.0", instructions: """
            MMail is the user's own mail app, running on their Mac. Message ids come from \
            list_messages / search_messages. To write mail: compose_message opens a composer on \
            screen that the user can see and edit; adjust it with update_compose and add_attachment, \
            then save_draft or send_message. Never send unless the user explicitly asked you to; \
            when in doubt, leave the composer open or save a draft. Archive, delete and spam show an \
            Undo toast in the app.
            """)
        let tools = Handlers(model: model)
        tools.register(on: server)
        return server
    }
}

@MainActor
private final class Handlers {
    let model: AppModel
    /// Messages seen through search that are not in the app's loaded folders.
    private var known: [String: Email] = [:]

    init(model: AppModel) { self.model = model }

    func register(on server: MCPServer) {
        let folderSchema = Schema.oneOf(AgentTools.folderIds, "Folder (default: inbox).")

        // MARK: Reading

        server.register("list_accounts", "List the connected mail accounts with their unread counts.") { [self] _ in
            let unread = model.unreadByAccount
            return [jsonContent(model.realConfigs.map { cfg -> JSON in
                ["id": cfg.id, "name": cfg.displayName, "email": cfg.email, "unread": unread[cfg.id] ?? 0]
            })]
        }

        server.register("list_folders", "List the folders of an account: standard folders plus every server mailbox (usable with move_message).",
                        properties: ["account": Schema.string("Account id (from list_accounts).")],
                        required: ["account"]) { [self] args in
            let account = try args.requireString("account")
            return [jsonContent(["standard": AgentTools.folderIds, "server_mailboxes": model.folderNames(for: account)])]
        }

        server.register("list_messages", "List messages in a folder, newest first.",
                        properties: [
                            "folder": folderSchema,
                            "account": Schema.string("Limit to one account (default: all accounts)."),
                            "unread_only": Schema.boolean("Only unread messages."),
                            "label": Schema.string("Only messages with this label id."),
                            "limit": Schema.integer("Maximum number of messages (default 30)."),
                            "refresh": Schema.boolean("Fetch new mail from the server first."),
                        ]) { [self] args in
            let folder = args.string("folder") ?? "inbox"
            let accounts = try scope(args.string("account"))
            await hydrate(folder: folder, accounts: accounts, force: args.bool("refresh") ?? false)
            var list = messages(in: folder, accounts: accounts)
            if args.bool("unread_only") == true { list = list.filter { $0.unread } }
            if let label = args.string("label") { list = list.filter { $0.labels.contains(label) } }
            return [jsonContent(list.prefix(args.int("limit") ?? 30).map(summary))]
        }

        server.register("search_messages", "Search mail by sender, subject or body, in the local cache and on the server.",
                        properties: [
                            "query": Schema.string("Words to look for."),
                            "folder": folderSchema,
                            "account": Schema.string("Limit to one account."),
                            "limit": Schema.integer("Maximum number of results (default 30)."),
                        ], required: ["query"]) { [self] args in
            let query = try args.requireString("query")
            let accounts = try scope(args.string("account"))
            let limit = args.int("limit") ?? 30
            var results = localSearch(query, accounts: accounts)
            let folder = args.string("folder") ?? "inbox"
            for account in accounts {
                guard let session = model.session(for: account) else { continue }
                let box = await serverMailbox(account, folder) ?? "INBOX"
                let hits = (try? await model.withTimeout(25) { try await session.searchText(mailbox: box, query: query, limit: limit) }) ?? []
                for m in hits {
                    let e = AppModel.makeEmail(m, accountId: account, folder: folder)
                    if !results.contains(where: { $0.id == e.id }) { results.append(e) }
                }
            }
            for e in results where find(e.id) == nil { known[e.id] = e }
            return [jsonContent(results.prefix(limit).map(summary))]
        }

        server.register("get_message", "Read a full message: headers, plain-text body (HTML on request) and attachment list.",
                        properties: [
                            "id": Schema.string("Message id."),
                            "include_html": Schema.boolean("Also return the HTML body."),
                            "mark_read": Schema.boolean("Mark as read (default false)."),
                        ], required: ["id"]) { [self] args in
            var email = try require(args.requireString("id"))
            if BodyFetch.needsFullFetch(bodyLoaded: email.bodyLoaded, bodyComplete: email.bodyComplete) {
                email = try await loadFull(email).email
            }
            if args.bool("mark_read") == true { setRead(email, true) }
            var out = summary(email)
            out["body"] = email.body
            if args.bool("include_html") == true, let html = email.bodyHTML { out["html"] = html }
            out["attachments"] = email.attachments.enumerated().map { i, a in
                ["index": i, "filename": a.filename, "mime_type": a.mimeType, "size": a.size] as JSON
            }
            if let ev = email.calendarEvent { out["calendar_event"] = ["summary": ev.summary, "location": ev.location ?? ""] }
            if email.unsubscribe != nil { out["has_unsubscribe"] = true }
            return [jsonContent(out)]
        }

        server.register("get_attachment",
                        "Open an attachment. Text comes back as text, images as images, anything else as a binary resource; the local file path is always given.",
                        properties: [
                            "id": Schema.string("Message id."),
                            "filename": Schema.string("Attachment file name (or use index)."),
                            "index": Schema.integer("Attachment index from get_message."),
                        ], required: ["id"]) { [self] args in
            let (meta, url) = try await attachmentFile(args)
            let data = try Data(contentsOf: url)
            let info = textContent("\(meta.filename) — \(meta.mimeType), \(data.count) bytes — \(url.path)")
            guard data.count <= 8 * 1024 * 1024 else {
                return [info, textContent("Too large to return inline; read the file at the path above.")]
            }
            if Mime.isText(meta.mimeType), let s = String(data: data, encoding: .utf8) {
                return [info, textContent(s)]
            }
            if meta.mimeType.hasPrefix("image/") {
                return [info, ["type": "image", "data": data.base64EncodedString(), "mimeType": meta.mimeType]]
            }
            return [info, ["type": "resource", "resource": [
                "uri": url.absoluteString, "mimeType": meta.mimeType, "blob": data.base64EncodedString(),
            ] as JSON]]
        }

        server.register("save_attachment", "Save an attachment to a file or folder on the Mac.",
                        properties: [
                            "id": Schema.string("Message id."),
                            "filename": Schema.string("Attachment file name (or use index)."),
                            "index": Schema.integer("Attachment index from get_message."),
                            "destination": Schema.string("Destination folder or file path (~ allowed)."),
                        ], required: ["id", "destination"]) { [self] args in
            let (meta, url) = try await attachmentFile(args)
            var dest = URL(fileURLWithPath: (try args.requireString("destination") as NSString).expandingTildeInPath)
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: dest.path, isDirectory: &isDir), isDir.boolValue {
                dest.appendPathComponent(meta.filename)
            }
            try FileManager.default.copyItem(at: url, to: dest)
            return [textContent("Saved to \(dest.path)")]
        }

        server.register("list_contacts", "People the user receives mail from, most recent first.",
                        properties: ["limit": Schema.integer("Maximum (default 50).")]) { [self] args in
            [jsonContent(model.contacts(limit: args.int("limit") ?? 50).map { ["name": $0.name, "email": $0.email] })]
        }

        server.register("list_labels", "List the user's labels.") { [self] _ in
            [jsonContent(model.labels.map { ["id": $0.id, "name": $0.name] })]
        }

        server.register("show_message", "Open a message in the app window so the user sees it.",
                        properties: ["id": Schema.string("Message id.")], required: ["id"]) { [self] args in
            model.openSearchResult(try require(args.requireString("id")))
            return [textContent("Shown in MMail.")]
        }

        // MARK: Organizing

        server.register("set_flags", "Mark a message read/unread and/or starred.",
                        properties: [
                            "id": Schema.string("Message id."),
                            "read": Schema.boolean("Read."),
                            "starred": Schema.boolean("Starred."),
                        ], required: ["id"]) { [self] args in
            let email = try inModel(args.requireString("id"))
            if let read = args.bool("read") { setRead(email, read) }
            if let starred = args.bool("starred"), let i = model.emails.firstIndex(where: { $0.id == email.id }) {
                model.emails[i].starred = starred
                model.applyRealFlag(model.emails[i], .flagged, add: starred)
            }
            return [textContent("OK")]
        }

        server.register("triage", "Archive, mark done, delete (to Trash) or mark a message as spam. The app shows an Undo toast.",
                        properties: [
                            "id": Schema.string("Message id."),
                            "action": Schema.oneOf(["archive", "done", "delete", "spam"], "What to do."),
                        ], required: ["id", "action"]) { [self] args in
            let email = try inModel(args.requireString("id"))
            switch try args.requireString("action") {
            case "archive": model.archive(email.id)
            case "done": model.markDone(email.id)
            case "delete": model.delete(email.id)
            case "spam": model.markSpam(email.id)
            default: throw MCPServer.ToolError("Unknown action.")
            }
            return [textContent("Done.")]
        }

        server.register("move_message", "Move a message to any server mailbox (see list_folders).",
                        properties: [
                            "id": Schema.string("Message id."),
                            "mailbox": Schema.string("Server mailbox name."),
                        ], required: ["id", "mailbox"]) { [self] args in
            let email = try inModel(args.requireString("id"))
            model.moveToMailbox(email.id, mailbox: try args.requireString("mailbox"))
            return [textContent("Moved.")]
        }

        server.register("snooze_message", "Hide a message until a given time.",
                        properties: [
                            "id": Schema.string("Message id."),
                            "until": Schema.string("ISO 8601 date-time, e.g. 2026-10-21T08:00:00+02:00."),
                        ], required: ["id", "until"]) { [self] args in
            let email = try inModel(args.requireString("id"))
            guard let date = ISO8601DateFormatter().date(from: try args.requireString("until")) else {
                throw MCPServer.ToolError("“until” must be an ISO 8601 date-time.")
            }
            model.snooze(email.id, until: date, label: "until \(date.formatted(date: .abbreviated, time: .shortened))")
            return [textContent("Snoozed.")]
        }

        server.register("label_message", "Add or remove a label on a message (created if needed).",
                        properties: [
                            "id": Schema.string("Message id."),
                            "label": Schema.string("Label name or id."),
                            "remove": Schema.boolean("Remove instead of add."),
                        ], required: ["id", "label"]) { [self] args in
            let email = try inModel(args.requireString("id"))
            let name = try args.requireString("label")
            let remove = args.bool("remove") ?? false
            let labelId = remove ? AppModel.sanitizeLabelId(name) : (model.addLabel(name) ?? AppModel.sanitizeLabelId(name))
            model.applyLabel(email, labelId, add: !remove)
            return [textContent(remove ? "Label removed." : "Label “\(labelId)” applied.")]
        }

        // MARK: Writing

        server.register("compose_message",
                        "Open a composer in the app, pre-filled. Optionally as a reply (reply_to_id) or a forward (forward_id, original attachments included). The user sees it on screen.",
                        properties: [
                            "to": Schema.strings("Recipients."),
                            "cc": Schema.strings("Cc."),
                            "bcc": Schema.strings("Bcc."),
                            "subject": Schema.string("Subject (derived automatically for replies and forwards)."),
                            "body": Schema.string("Plain-text body you write; the signature and any quote are added after it."),
                            "from_account": Schema.string("Sending account id (default: the message's account, or the first one)."),
                            "reply_to_id": Schema.string("Message to reply to."),
                            "reply_all": Schema.boolean("Reply to everyone."),
                            "forward_id": Schema.string("Message to forward."),
                            "replace_open": Schema.boolean("Replace a composer the user already has open."),
                        ]) { [self] args in
            if let open = snapshot(), !isBlank(open), args.bool("replace_open") != true {
                throw MCPServer.ToolError("The user already has a composer open. Read it with get_compose, or pass replace_open: true.")
            }
            var to = args.recipients("to") ?? ""
            var subject = args.string("subject") ?? ""
            var tail = ""
            var attachments: [ComposeAttachment] = []
            var from = args.string("from_account") ?? model.accounts.first?.id ?? ""
            var title = "Claude · New message"

            if let id = args.string("reply_to_id") {
                let original = try await loadFull(try require(id)).email
                let sender = original.resolvedSender
                from = args.string("from_account") ?? original.account
                if to.isEmpty {
                    var list = [sender.email]
                    if args.bool("reply_all") == true {
                        let me = model.config(for: original.account)?.email.lowercased() ?? ""
                        list += AppModel.parseRecipients((original.to ?? []).joined(separator: ", ")).filter { $0.lowercased() != me }
                    }
                    to = list.filter { !$0.isEmpty }.joined(separator: ", ")
                }
                if subject.isEmpty { subject = original.subject.hasPrefix("Re:") ? original.subject : "Re: \(original.subject)" }
                tail = model.quotedBody(original)
                title = "Claude · Reply · \(sender.name)"
            } else if let id = args.string("forward_id") {
                let (original, parsed) = try await loadFull(try require(id))
                from = args.string("from_account") ?? original.account
                if subject.isEmpty { subject = original.subject.hasPrefix("Fwd:") ? original.subject : "Fwd: \(original.subject)" }
                tail = "\n\n--- Forwarded message ---\n\(original.body)"
                attachments = (parsed?.attachments ?? []).map { ComposeAttachment(filename: $0.filename, mimeType: $0.mimeType, data: $0.data) }
                title = "Claude · Forward"
            }

            let signature = model.signature(for: from)
            let body = (args.string("body") ?? "") + (signature.isEmpty ? "" : "\n\n-- \n\(signature)") + tail
            let draft = ComposeDraft(to: to, cc: args.recipients("cc") ?? "", bcc: args.recipients("bcc") ?? "",
                                     subject: subject, body: body, titleLabel: title, fromId: from,
                                     attachments: attachments)
            model.compose = draft
            return [jsonContent(describe(draft))]
        }

        server.register("get_compose", "Read the composer currently open in the app, including the user's own edits.") { [self] _ in
            guard let draft = snapshot() else { return [textContent("No composer is open.")] }
            return [jsonContent(describe(draft))]
        }

        server.register("update_compose", "Change fields of the open composer; only the fields given change.",
                        properties: [
                            "to": Schema.strings("Recipients."),
                            "cc": Schema.strings("Cc."),
                            "bcc": Schema.strings("Bcc."),
                            "subject": Schema.string("Subject."),
                            "body": Schema.string("Replace the whole body."),
                            "append_body": Schema.string("Text appended at the end of the body."),
                            "from_account": Schema.string("Sending account id."),
                        ]) { [self] args in
            var draft = try requireCompose()
            if let v = args.recipients("to") { draft.to = v }
            if let v = args.recipients("cc") { draft.cc = v }
            if let v = args.recipients("bcc") { draft.bcc = v }
            if let v = args["subject"] as? String { draft.subject = v }
            if let v = args["body"] as? String { draft.body = v; draft.bodyHTML = nil }
            if let v = args.string("append_body") { draft.body += v; draft.bodyHTML = nil }
            if let v = args.string("from_account") { draft.fromId = v }
            return [jsonContent(describe(reopen(draft)))]
        }

        server.register("add_attachment",
                        "Attach a file to the open composer: a local file (file_path), text (text + filename) or bytes (content_base64 + filename).",
                        properties: [
                            "file_path": Schema.string("File on the Mac (~ allowed)."),
                            "filename": Schema.string("Attachment name (required with text or content_base64)."),
                            "text": Schema.string("Text content to attach."),
                            "content_base64": Schema.string("Binary content, base64."),
                            "mime_type": Schema.string("MIME type (guessed from the extension otherwise)."),
                        ]) { [self] args in
            var draft = try requireCompose()
            let data: Data
            let filename: String
            if let path = args.string("file_path") {
                let url = URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
                data = try Data(contentsOf: url)
                filename = args.string("filename") ?? url.lastPathComponent
            } else if let text = args["text"] as? String {
                data = Data(text.utf8)
                filename = try args.requireString("filename")
            } else if let b64 = args.string("content_base64") {
                guard let decoded = Data(base64Encoded: b64, options: .ignoreUnknownCharacters) else {
                    throw MCPServer.ToolError("Invalid base64.")
                }
                data = decoded
                filename = try args.requireString("filename")
            } else {
                throw MCPServer.ToolError("Give file_path, text or content_base64.")
            }
            draft.attachments.append(ComposeAttachment(filename: filename,
                                                       mimeType: args.string("mime_type") ?? Mime.guess(filename),
                                                       data: data))
            return [jsonContent(describe(reopen(draft)))]
        }

        server.register("remove_attachment", "Remove an attachment from the open composer.",
                        properties: ["filename": Schema.string("Attachment file name.")],
                        required: ["filename"]) { [self] args in
            var draft = try requireCompose()
            let name = try args.requireString("filename")
            guard draft.attachments.contains(where: { $0.filename == name }) else { throw MCPServer.ToolError("No attachment named \(name).") }
            draft.attachments.removeAll { $0.filename == name }
            return [jsonContent(describe(reopen(draft)))]
        }

        server.register("save_draft", "Save the open composer to the Drafts folder and close it.") { [self] _ in
            model.saveDraftAndClose(try requireCompose())
            return [textContent("Draft saved.")]
        }

        server.register("send_message",
                        "Send the open composer. Only when the user explicitly asked to send. The app keeps a 5-second Undo before it goes out.",
                        properties: ["confirm": Schema.boolean("Must be true: the user asked for this message to be sent.")],
                        required: ["confirm"]) { [self] args in
            guard args.bool("confirm") == true else { throw MCPServer.ToolError("Set confirm: true once the user has asked to send.") }
            let draft = try requireCompose()
            guard !AppModel.parseRecipients([draft.to, draft.cc, draft.bcc].joined(separator: ",")).isEmpty else {
                throw MCPServer.ToolError("The message has no recipient.")
            }
            model.sendDraft(draft)
            return [textContent("Sending to \(draft.to). The user can still undo it for 5 seconds in the app.")]
        }
    }

    // MARK: - Lookup

    private func scope(_ account: String?) throws -> [String] {
        guard let account else { return model.realConfigs.map { $0.id } }
        guard model.isRealAccount(account) else { throw MCPServer.ToolError("Unknown account \(account).") }
        return [account]
    }

    private func find(_ id: String) -> Email? {
        model.emails.first { $0.id == id }
            ?? model.serverSearchResults?.first { $0.id == id }
            ?? known[id]
    }

    private func require(_ id: String) throws -> Email {
        if let e = find(id) { return e }
        if let e = MailCache.loadAll().first(where: { $0.id == id }) { known[id] = e; return e }
        throw MCPServer.ToolError("Message \(id) not found. Use list_messages or search_messages first.")
    }

    /// Triage actions work on `model.emails`; bring search hits in first.
    private func inModel(_ id: String) throws -> Email {
        let e = try require(id)
        if !model.emails.contains(where: { $0.id == id }) { model.emails.append(e) }
        return e
    }

    private func messages(in folder: String, accounts: [String]) -> [Email] {
        model.emails.filter { e in
            accounts.contains(e.account)
                && (folder == "starred" ? (e.starred && e.folder != "trash") : e.folder == folder)
        }
        .sorted { ($0.uid ?? 0) > ($1.uid ?? 0) }
    }

    private func localSearch(_ query: String, accounts: [String]) -> [Email] {
        let q = query.lowercased()
        var byId: [String: Email] = [:]
        for e in MailCache.loadAll() { byId[e.id] = e }
        for e in model.emails { byId[e.id] = e }
        return byId.values.filter { e in
            accounts.contains(e.account) && (
                e.subject.lowercased().contains(q) || e.body.lowercased().contains(q)
                    || e.preview.lowercased().contains(q)
                    || (e.fromName?.lowercased().contains(q) ?? false)
                    || (e.fromEmail?.lowercased().contains(q) ?? false)
                    || (e.to ?? []).contains { $0.lowercased().contains(q) })
        }
        .sorted { ($0.uid ?? 0) > ($1.uid ?? 0) }
    }

    /// Make sure a folder is in memory (cache or server) before listing it.
    private func hydrate(folder: String, accounts: [String], force: Bool) async {
        for account in accounts {
            model.loadFolder(account, folder, silent: false, force: force)
        }
        let deadline = Date().addingTimeInterval(30)
        try? await Task.sleep(nanoseconds: 300_000_000)
        while Date() < deadline, accounts.contains(where: { model.loadingAccounts.contains($0) }) {
            try? await Task.sleep(nanoseconds: 250_000_000)
        }
    }

    private func serverMailbox(_ account: String, _ folder: String) async -> String? {
        if let name = model.mailboxName(account, folder == "done" ? "archive" : folder) { return name }
        guard let session = model.session(for: account) else { return nil }
        let kinds: [String: MailboxKind] = ["sent": .sent, "drafts": .drafts, "trash": .trash, "spam": .junk,
                                            "archive": .archive, "done": .archive]
        guard let kind = kinds[folder] else { return nil }
        return await model.resolveMailbox(account, kind: kind, session: session)
    }

    // MARK: - Bodies and attachments

    /// Fetches the whole message and stores the parsed body in the app model.
    private func loadFull(_ email: Email) async throws -> (email: Email, parsed: MIME.Parsed?) {
        guard model.isRealAccount(email.account), let uid = email.uid,
              let session = model.bodySession(for: email.account) else { return (email, nil) }
        let box = await serverMailbox(email.account, email.folder) ?? "INBOX"
        let data = try await model.withTimeout(30) {
            try await session.fetchMessageData(mailbox: box, uid: uid, byteLimit: nil)
        }
        let parsed = MIME.parse(data)
        var updated = email
        updated.body = parsed.text
        updated.preview = String(parsed.text.replacingOccurrences(of: "\n", with: " ").prefix(140))
        updated.bodyLoaded = true
        updated.bodyComplete = true
        updated.bodyHTML = parsed.html
        updated.attachments = parsed.attachments.map { AttachmentMeta(filename: $0.filename, mimeType: $0.mimeType, size: $0.data.count) }
        updated.hasAttachment = !updated.attachments.isEmpty
        updated.unsubscribe = parsed.listUnsubscribe
        updated.calendarEvent = parsed.calendar.flatMap { MIME.parseICS($0) }
        if let i = model.emails.firstIndex(where: { $0.id == email.id }) {
            model.emails[i] = updated
            MailCache.save(model.emails.filter { $0.account == updated.account && $0.folder == updated.folder },
                           account: updated.account, folder: updated.folder)
        } else {
            known[email.id] = updated
        }
        for att in parsed.attachments {
            let meta = AttachmentMeta(filename: att.filename, mimeType: att.mimeType, size: att.data.count)
            let url = model.attachmentCacheURL(updated, meta)
            try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? att.data.write(to: url)
        }
        return (updated, parsed)
    }

    private func attachmentFile(_ args: JSON) async throws -> (AttachmentMeta, URL) {
        var email = try require(args.requireString("id"))
        func pick(_ e: Email) -> AttachmentMeta? {
            if let name = args.string("filename") { return e.attachments.first { $0.filename == name } }
            if let i = args.int("index"), e.attachments.indices.contains(i) { return e.attachments[i] }
            return e.attachments.count == 1 ? e.attachments.first : nil
        }
        if let meta = pick(email) {
            let url = model.attachmentCacheURL(email, meta)
            if FileManager.default.fileExists(atPath: url.path) { return (meta, url) }
        }
        email = try await loadFull(email).email
        guard let meta = pick(email) else {
            let names = email.attachments.map(\.filename).joined(separator: ", ")
            throw MCPServer.ToolError(names.isEmpty ? "This message has no attachments." : "Say which attachment: \(names).")
        }
        let url = model.attachmentCacheURL(email, meta)
        guard FileManager.default.fileExists(atPath: url.path) else { throw MCPServer.ToolError("Could not download the attachment.") }
        return (meta, url)
    }

    private func setRead(_ email: Email, _ read: Bool) {
        guard let i = model.emails.firstIndex(where: { $0.id == email.id }) else { return }
        model.emails[i].unread = !read
        model.applyRealFlag(model.emails[i], .seen, add: read)
    }

    // MARK: - Composer

    private func snapshot() -> ComposeDraft? {
        if let live = model.composeSnapshot, live.id == model.compose?.id { return live.read() }
        return model.compose
    }

    private func requireCompose() throws -> ComposeDraft {
        guard let draft = snapshot() else { throw MCPServer.ToolError("No composer is open. Use compose_message first.") }
        return draft
    }

    private func isBlank(_ d: ComposeDraft) -> Bool {
        d.to.isEmpty && d.subject.isEmpty && d.body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && d.attachments.isEmpty
    }

    /// Show an edited draft: a fresh id makes the composer reload its fields.
    @discardableResult
    private func reopen(_ d: ComposeDraft) -> ComposeDraft {
        var fresh = ComposeDraft(to: d.to, cc: d.cc, bcc: d.bcc, subject: d.subject, body: d.body,
                                 titleLabel: d.titleLabel, fromId: d.fromId, attachments: d.attachments,
                                 bodyHTML: d.bodyHTML)
        fresh.originalDraftId = d.originalDraftId
        model.compose = fresh
        return fresh
    }

    private func describe(_ d: ComposeDraft) -> JSON {
        [
            "from": model.config(for: d.fromId)?.email ?? d.fromId,
            "to": d.to, "cc": d.cc, "bcc": d.bcc, "subject": d.subject, "body": d.body,
            "attachments": d.attachments.map { ["filename": $0.filename, "mime_type": $0.mimeType, "size": $0.data.count] as JSON },
        ]
    }

    private func summary(_ e: Email) -> JSON {
        let sender = e.resolvedSender
        let from = e.from == "you" ? (model.config(for: e.account)?.email ?? "me")
            : (sender.email.isEmpty ? sender.name : "\(sender.name) <\(sender.email)>")
        var out: JSON = [
            "id": e.id, "account": e.account, "folder": e.folder, "from": from, "to": e.to ?? [],
            "subject": e.subject, "date": e.time, "day": e.day, "unread": e.unread,
            "starred": e.starred, "has_attachments": e.hasAttachment || !e.attachments.isEmpty, "labels": e.labels,
        ]
        if !e.preview.isEmpty { out["preview"] = e.preview }
        return out
    }
}

enum Mime {
    private static let table: [String: String] = [
        "txt": "text/plain", "md": "text/markdown", "csv": "text/csv", "html": "text/html", "ics": "text/calendar",
        "json": "application/json", "xml": "application/xml", "pdf": "application/pdf", "zip": "application/zip",
        "png": "image/png", "jpg": "image/jpeg", "jpeg": "image/jpeg", "gif": "image/gif", "heic": "image/heic",
        "webp": "image/webp", "svg": "image/svg+xml", "doc": "application/msword",
        "docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "pptx": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    ]

    static func guess(_ filename: String) -> String {
        table[(filename as NSString).pathExtension.lowercased()] ?? "application/octet-stream"
    }

    static func isText(_ mime: String) -> Bool {
        mime.hasPrefix("text/") || ["application/json", "application/xml"].contains(mime)
    }
}
