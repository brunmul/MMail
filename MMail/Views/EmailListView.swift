import SwiftUI

struct EmailListView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.palette) private var p

    private var isSearch: Bool {
        model.searchActive && !model.searchQuery.trimmingCharacters(in: .whitespaces).isEmpty
    }
    private var folderName: String {
        SampleData.folders.first { $0.id == model.folder }?.name ?? model.folder
    }
    private var scopeLoading: Bool {
        model.currentAccount == "all" ? !model.loadingAccounts.isEmpty : model.loadingAccounts.contains(model.currentAccount)
    }
    private var scopeIsRealMail: Bool {
        model.isRealAccount(model.currentAccount) || (model.currentAccount == "all" && !model.realConfigs.isEmpty)
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider().overlay(p.border)
            if model.selectionActive {
                selectionBar
                Divider().overlay(p.border)
            }
            list
        }
        .frame(width: model.readingPane ? 380 : nil)
        .frame(maxWidth: model.readingPane ? 380 : .infinity, maxHeight: .infinity)
        .background(p.bg1)
        .overlay(alignment: .trailing) {
            if model.readingPane { Rectangle().fill(p.border).frame(width: 1) }
        }
    }

    // MARK: Header

    private var header: some View {
        let visible = model.filteredEmails
        let unread = visible.filter { $0.unread }.count
        let acctName = model.currentAccount == "all" ? "All inboxes" : (model.accountsById[model.currentAccount]?.name ?? folderName)
        let labelName = model.labelFilter.flatMap { model.label(for: $0)?.name }
        let title = isSearch ? "Search" : (labelName ?? (model.folder == "inbox" ? acctName : folderName))
        let sub: String = {
            if isSearch {
                let n = visible.count
                return "\(n) result\(n == 1 ? "" : "s") for \"\(model.searchQuery)\""
            }
            if labelName != nil {
                let n = visible.count
                return "\(n) \(n == 1 ? "message" : "messages") · label"
            }
            if model.folder == "inbox" {
                return unread > 0 ? "\(unread) unread · \(greeting())" : "Inbox zero · \(greeting())"
            }
            let n = visible.count
            return "\(n) \(n == 1 ? "message" : "messages")"
        }()

        let total = visible.count
        return VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 4) {
                Button {
                    if model.selectionActive { model.clearSelection() } else { model.selectAllVisible() }
                } label: {
                    Image(systemName: model.selectionActive ? "checkmark.square.fill" : "square")
                        .font(.system(size: 15))
                        .foregroundStyle(model.selectionActive ? p.brandBlue : p.fg2)
                        .frame(width: 32, height: 32)
                }
                .buttonStyle(.plain)
                .help(model.selectionActive ? "Deselect all" : "Select all")
                if scopeIsRealMail {
                    Button { model.userRefresh() } label: {
                        Group {
                            if model.refreshing || scopeLoading {
                                ProgressView().controlSize(.small)
                            } else {
                                Icon(name: "refresh", size: 15).foregroundStyle(p.fg2)
                            }
                        }
                        .frame(width: 32, height: 32)
                    }
                    .buttonStyle(.plain)
                    .disabled(model.refreshing)
                    .help(model.refreshing ? "Refreshing…" : "Refresh")
                }
                if isSearch || labelName != nil || model.folder != "inbox" {
                    Text(isSearch ? sub : title)
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(p.fg2)
                        .lineLimit(1)
                        .padding(.leading, 8)
                }
                if model.searching {
                    ProgressView().controlSize(.small).scaleEffect(0.7)
                }
                Spacer()
                if total > 0 {
                    Text("1–\(total) of \(total)")
                        .font(.system(size: 12))
                        .monospacedDigit()
                        .foregroundStyle(p.fg3)
                        .padding(.trailing, 8)
                }
            }
            .padding(.horizontal, 12)
            .frame(height: 48)
            if model.folder == "inbox" && !isSearch && model.labelFilter == nil {
                HStack(spacing: 0) {
                    ForEach(InboxFilter.allCases, id: \.self) { f in
                        inboxTab(f, unread: unread)
                    }
                    if !model.readingPane { Spacer() }
                }
                .overlay(Rectangle().fill(p.border).frame(height: 1), alignment: .bottom)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var selectionBar: some View {
        HStack(spacing: 6) {
            Text("\(model.selectedIds.count) selected")
                .font(.system(size: 12.5, weight: .semibold)).foregroundStyle(p.fg1)
            Button { model.selectAllVisible() } label: {
                Text("Select all").font(.system(size: 11.5)).foregroundStyle(p.brandBlue)
            }.buttonStyle(.plain)
            Spacer()
            bulkButton("archive", "Archive") { model.bulkArchive() }
            let moveAcct = model.currentAccount == "all"
                ? (model.emails.first { model.selectedIds.contains($0.id) }?.account ?? "")
                : model.currentAccount
            let folders = model.folderNames(for: moveAcct)
            if !folders.isEmpty {
                Menu {
                    ForEach(folders, id: \.self) { name in
                        Button(name) { model.bulkMoveToMailbox(name) }
                    }
                } label: {
                    Icon(name: "outbox", size: 14).foregroundStyle(p.fg2).frame(width: 28, height: 26)
                }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize().help("Move to…")
            }
            bulkButton("mail", "Read") { model.bulkMarkRead(true) }
            bulkButton("trash", "Delete") { model.bulkDelete() }
            Button { model.clearSelection() } label: {
                Icon(name: "x", size: 13).foregroundStyle(p.fg3).frame(width: 26, height: 26)
            }.buttonStyle(.plain).help("Clear selection (esc)")
        }
        .padding(.horizontal, 20).padding(.vertical, 8)
        .background(p.bg2)
    }

    private func bulkButton(_ icon: String, _ help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Icon(name: icon, size: 14).foregroundStyle(p.fg2).frame(width: 28, height: 26)
        }
        .buttonStyle(.plain).help(help)
    }

    private func inboxTab(_ f: InboxFilter, unread: Int) -> some View {
        let active = model.filter == f
        let icon: String = {
            switch f {
            case .all: return "tray"
            case .unread: return "envelope.badge"
            case .people: return "person.2"
            case .updates: return "info.circle"
            }
        }()
        return Button { model.filter = f } label: {
            HStack(spacing: 12) {
                Image(systemName: icon).font(.system(size: 15))
                Text(f.title).font(.system(size: 13.5, weight: active ? .semibold : .regular))
                if f == .unread && unread > 0 && !active {
                    Text("\(unread) new")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 7).padding(.vertical, 1)
                        .background(p.brandBlue)
                        .clipShape(Capsule())
                }
            }
            .foregroundStyle(active ? p.brandBlue : p.fg2)
            .padding(.horizontal, 16)
            .frame(width: model.readingPane ? nil : 240, height: 52, alignment: .leading)
            .frame(maxWidth: model.readingPane ? .infinity : nil, alignment: .leading)
            .contentShape(Rectangle())
            .overlay(alignment: .bottom) {
                if active {
                    UnevenRoundedRectangle(topLeadingRadius: 3, topTrailingRadius: 3)
                        .fill(p.brandBlue)
                        .frame(height: 3)
                        .padding(.horizontal, 8)
                }
            }
        }
        .buttonStyle(.plain)
    }

    // MARK: List

    private var list: some View {
        let visible = model.filteredEmails
        return Group {
            if visible.isEmpty && scopeLoading {
                loadingState
            } else if visible.isEmpty, let err = model.accountErrors[model.currentAccount] {
                errorState(err)
            } else if visible.isEmpty {
                emptyState
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(spacing: 0, pinnedViews: []) {
                            if !model.readingPane {
                                ForEach(visible) { e in
                                    GmailRowView(email: e,
                                                 showAccountDot: model.currentAccount == "all",
                                                 accountColor: model.accountsById[e.account]?.color)
                                        .id(e.id)
                                }
                            } else {
                            ForEach(groupByDay(visible), id: \.0) { day, items in
                                Text(day.uppercased())
                                    .font(.system(size: 10.5, weight: .bold))
                                    .tracking(0.6)
                                    .foregroundStyle(p.fg4)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .padding(.horizontal, 20)
                                    .padding(.top, 14)
                                    .padding(.bottom, 6)
                                ForEach(items) { e in
                                    EmailRowView(email: e,
                                                 selected: e.id == model.selectedEmail?.id,
                                                 showAccountDot: model.currentAccount == "all",
                                                 accountColor: model.accountsById[e.account]?.color)
                                        .id(e.id)
                                }
                            }
                            }
                            if model.canLoadMore || model.downloadingAllOlder {
                                loadOlderFooter
                            }
                        }
                    }
                    .onChange(of: model.selectedId) { _, id in
                        if let id { withAnimation { proxy.scrollTo(id, anchor: .center) } }
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var loadingState: some View {
        VStack(spacing: 12) {
            ProgressView().controlSize(.large)
            Text("Connecting…").font(.system(size: 13.5)).foregroundStyle(p.fg3)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func errorState(_ message: String) -> some View {
        VStack(spacing: 10) {
            Icon(name: "alert", size: 32, weight: .light).foregroundStyle(p.danger)
            Text("Couldn't connect").font(.system(size: 18, weight: .bold)).foregroundStyle(p.fg2)
            Text(message).font(.system(size: 12.5)).foregroundStyle(p.fg3)
                .multilineTextAlignment(.center).frame(maxWidth: 320)
            Button { model.userRefresh() } label: {
                HStack(spacing: 6) {
                    if model.refreshing { ProgressView().controlSize(.small).colorScheme(.dark) }
                    Text(model.refreshing ? "Retrying…" : "Retry")
                        .font(.system(size: 12.5, weight: .semibold))
                        .foregroundStyle(.white)
                }
                .padding(.horizontal, 14).padding(.vertical, 7)
                .background(p.brandBlue).clipShape(Capsule())
            }
            .buttonStyle(.plain)
            .disabled(model.refreshing)
            .padding(.top, 4)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity).padding(40)
    }

    private var emptyState: some View {
        VStack(spacing: 0) {
            Icon(name: "done", size: 36, weight: .light)
                .foregroundStyle(p.fg2)
            Text(model.folder == "inbox" ? "You're all caught up" : "Nothing here")
                .font(.system(size: 22, weight: .bold))
                .foregroundStyle(p.fg2)
                .padding(.top, 16).padding(.bottom, 8)
            Text(model.folder == "inbox"
                 ? "No new mail in your inbox. Take a break, or press C to write something."
                 : "No messages in \(folderName.lowercased()).")
                .font(.system(size: 13.5))
                .foregroundStyle(p.fg3)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 360)
            if model.canLoadMore || model.downloadingAllOlder {
                loadOlderFooter
                    .padding(.top, 22)
                    .frame(maxWidth: 360)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(40)
    }

    /// Shared footer shown both below the inbox list and inside the empty
    /// state: "Load older messages" for one page + "Download all older
    /// messages" for an automated recursive backfill. Swaps to a progress
    /// line with a Cancel button while a recursive download is running.
    private var loadOlderFooter: some View {
        VStack(spacing: 6) {
            if model.downloadingAllOlder {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Downloading all messages… \(model.olderDownloadedCount) downloaded")
                        .font(.system(size: 12.5, weight: .semibold))
                        .foregroundStyle(p.fg2)
                        .multilineTextAlignment(.center)
                }
                Button("Cancel") { model.cancelLoadAllOlder() }
                    .buttonStyle(.plain)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(p.brandBlue)
            } else {
                Button { model.loadOlder() } label: {
                    HStack(spacing: 6) {
                        if model.loadingOlder { ProgressView().controlSize(.small) }
                        Text(model.loadingOlder ? "Loading more…" : "Load more")
                            .font(.system(size: 12.5, weight: .semibold))
                            .foregroundStyle(p.brandBlue)
                    }
                    .frame(maxWidth: .infinity).padding(.vertical, 10)
                }
                .buttonStyle(.plain)
                .disabled(model.loadingOlder)

                Button { model.loadAllOlder() } label: {
                    Text("Download all messages")
                        .font(.system(size: 11.5))
                        .foregroundStyle(p.fg3)
                        .underline()
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.plain)
                .disabled(model.loadingOlder)
            }
        }
        .padding(.vertical, 10)
    }

    // MARK: Grouping

    private func groupByDay(_ emails: [Email]) -> [(String, [Email])] {
        let order = ["today", "yesterday", "earlier", "snoozed"]
        let labels = ["today": "Today", "yesterday": "Yesterday", "earlier": "Earlier", "snoozed": "Snoozed"]
        var buckets: [String: [Email]] = [:]
        for e in emails { buckets[e.day, default: []].append(e) }
        return order.compactMap { key in
            guard let items = buckets[key], !items.isEmpty else { return nil }
            return (labels[key] ?? key, items)
        }
    }
}

func greeting() -> String {
    let h = Calendar.current.component(.hour, from: Date())
    if h < 5 { return "burning the midnight oil" }
    if h < 12 { return "good morning" }
    if h < 18 { return "good afternoon" }
    return "good evening"
}

// MARK: - Email row

struct EmailRowView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.palette) private var p
    let email: Email
    let selected: Bool
    let showAccountDot: Bool
    let accountColor: Color?
    @State private var hovered = false

    private var sender: Sender? { email.resolvedSender }

    private var bulkSelected: Bool { model.selectedIds.contains(email.id) }

    /// Outgoing folders show the recipient; everything else shows the sender.
    private var displayName: String {
        if ["sent", "drafts", "outbox"].contains(email.folder) {
            guard let to = email.to?.first, !to.isEmpty else { return "(no recipient)" }
            if let lt = to.firstIndex(of: "<") {
                let name = String(to[..<lt]).trimmingCharacters(in: .whitespaces)
                return name.isEmpty ? to.trimmingCharacters(in: CharacterSet(charactersIn: "<> ")) : name
            }
            return to
        }
        return sender?.name ?? (email.to?.first ?? "You")
    }

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            if hovered || model.selectionActive {
                Button { model.toggleSelect(email.id) } label: {
                    ZStack {
                        RoundedRectangle(cornerRadius: 5, style: .continuous)
                            .fill(bulkSelected ? p.brandBlue : Color.clear)
                            .overlay(RoundedRectangle(cornerRadius: 5, style: .continuous)
                                .stroke(bulkSelected ? p.brandBlue : p.borderStrong, lineWidth: 1.5))
                            .frame(width: 18, height: 18)
                        if bulkSelected { Icon(name: "check", size: 11, weight: .bold).foregroundStyle(.white) }
                    }
                    .frame(width: 22, height: 22)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .padding(.top, 7)
            }
            HStack(alignment: .top, spacing: 12) {
            avatar
            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    HStack(spacing: 6) {
                        if model.isVIP(email.fromEmail) {
                            Icon(name: "crown", size: 10).foregroundStyle(Color(hex: "F4A52A"))
                        }
                        Text(displayName)
                            .font(.system(size: 13.5, weight: email.unread ? .bold : .semibold))
                            .foregroundStyle(email.unread ? p.fg1 : p.fg2)
                            .lineLimit(1)
                    }
                    Spacer(minLength: 4)
                    if email.starred {
                        Icon(name: "star.fill", size: 10).foregroundStyle(Color(hex: "F4A52A"))
                    }
                    Text(email.time)
                        .font(.system(size: 11.5, weight: .medium))
                        .monospacedDigit()
                        .foregroundStyle(email.unread ? p.brandBlue : p.fg3)
                }
                Text(email.subject)
                    .font(.system(size: 13, weight: email.unread ? .bold : .regular))
                    .foregroundStyle(email.unread ? p.fg1 : p.fg2)
                    .lineLimit(1)
                Text(email.preview)
                    .font(.system(size: 12.5))
                    .foregroundStyle(p.fg3)
                    .lineLimit(2)
                    .lineSpacing(1)
                if !email.labels.isEmpty || email.hasAttachment {
                    HStack(spacing: 6) {
                        if email.hasAttachment {
                            Icon(name: "attach", size: 12).foregroundStyle(p.fg3)
                        }
                        ForEach(email.labels, id: \.self) { l in
                            if let def = model.label(for: l) {
                                Pill(label: def.name, kind: l, colorHex: def.colorHex)
                            }
                        }
                    }
                    .padding(.top, 4)
                }
            }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
            .onTapGesture {
                if model.selectionActive { model.toggleSelect(email.id) } else { model.activate(email.id) }
            }
            if hovered || selected {
                VStack {
                    HStack(spacing: 2) {
                        rowAction(email.starred ? "star.fill" : "star", help: "Star (S)") { model.toggleStar(email.id) }
                        rowAction("archive", help: "Archive (E)") { model.archive(email.id) }
                    }
                    Spacer()
                }
            }
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 14)
        .background(rowBackground)
        .overlay(alignment: .leading) {
            if selected {
                Rectangle().fill(p.brandBlue).frame(width: 3)
                    .padding(.vertical, 8)
                    .clipShape(RoundedRectangle(cornerRadius: 2))
            }
        }
        .overlay(Rectangle().fill(p.border).frame(height: 1), alignment: .bottom)
        .onHover { hovered = $0 }
        .contextMenu { EmailContextMenu(email: email) }
    }

    private var avatar: some View {
        Avatar(sender: sender, size: 36)
    }

    private var rowBackground: Color {
        // No orange "active" wash: a checked row gets a neutral fill, the open one the hover fill.
        if bulkSelected { return Arcos.chipFill(p) }
        if selected || hovered { return Arcos.hoverFill(p) }
        return .clear
    }

    private func rowAction(_ icon: String, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Icon(name: icon, size: 14)
                .foregroundStyle(p.fg3)
                .frame(width: 26, height: 26)
                .background(p.bg3.opacity(0.0001))
                .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
        }
        .buttonStyle(.plain)
        .help(help)
    }
}

// MARK: - Row context menu (shared by both row styles)

/// Right-click menu for a row. Reply/forward set the selection first since
/// those actions read from `selectedEmail`; triage actions take an explicit id.
struct EmailContextMenu: View {
    @EnvironmentObject var model: AppModel
    let email: Email
    private var sender: Sender? { email.resolvedSender }

    var body: some View {
        Button { model.selectedId = email.id; model.reply() } label: {
            Label("Reply", systemImage: "arrowshape.turn.up.left")
        }
        Button { model.selectedId = email.id; model.replyAll() } label: {
            Label("Reply all", systemImage: "arrowshape.turn.up.left.2")
        }
        Button { model.selectedId = email.id; model.forward() } label: {
            Label("Forward", systemImage: "arrowshape.turn.up.right")
        }
        Divider()
        Button { model.archive(email.id) } label: {
            Label("Archive", systemImage: "archivebox")
        }
        Button { model.toggleStar(email.id) } label: {
            Label(email.starred ? "Unstar" : "Star", systemImage: email.starred ? "star.slash" : "star")
        }
        Button { model.toggleRead(email.id) } label: {
            Label(email.unread ? "Mark as read" : "Mark as unread", systemImage: email.unread ? "envelope.open" : "envelope.badge")
        }
        Button { model.snooze(email.id) } label: {
            Label("Snooze until tomorrow", systemImage: "clock")
        }
        Divider()
        let mailboxes = model.folderNames(for: email.account)
        if !mailboxes.isEmpty {
            Menu {
                ForEach(mailboxes, id: \.self) { name in
                    Button(name) { model.moveToMailbox(email.id, mailbox: name) }
                }
            } label: { Label("Move to…", systemImage: "tray.and.arrow.up") }
        }
        Button { model.markSpam(email.id) } label: {
            Label("Mark as spam", systemImage: "exclamationmark.triangle")
        }
        if let addr = email.fromEmail, !addr.isEmpty {
            Button { model.toggleVIP(addr) } label: {
                Label(model.isVIP(addr) ? "Remove VIP" : "Mark sender as VIP", systemImage: "crown")
            }
            Button(role: .destructive) { model.blockSender(addr) } label: {
                Label("Block \(sender?.firstName ?? addr)", systemImage: "hand.raised")
            }
        }
        Divider()
        Button(role: .destructive) { model.delete(email.id) } label: {
            Label("Delete", systemImage: "trash")
        }
    }
}

// MARK: - Gmail-style single-line row (reading pane off)

struct GmailRowView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.palette) private var p
    let email: Email
    let showAccountDot: Bool
    let accountColor: Color?
    @State private var hovered = false

    private var bulkSelected: Bool { model.selectedIds.contains(email.id) }
    private var keyboardSelected: Bool { email.id == model.selectedId }

    private var displayName: String {
        if ["sent", "drafts", "outbox"].contains(email.folder) {
            guard let to = email.to?.first, !to.isEmpty else { return "(no recipient)" }
            if let lt = to.firstIndex(of: "<") {
                let name = String(to[..<lt]).trimmingCharacters(in: .whitespaces)
                if !name.isEmpty { return "To: \(name)" }
            }
            return "To: \(to)"
        }
        return email.resolvedSender.name
    }

    var body: some View {
        HStack(spacing: 0) {
            Button { model.toggleSelect(email.id) } label: {
                Image(systemName: bulkSelected ? "checkmark.square.fill" : "square")
                    .font(.system(size: 15))
                    .foregroundStyle(bulkSelected ? p.brandBlue : p.fg4)
                    .frame(width: 30, height: 40)
            }
            .buttonStyle(.plain)
            Button { model.toggleStar(email.id) } label: {
                Image(systemName: email.starred ? "star.fill" : "star")
                    .font(.system(size: 14))
                    .foregroundStyle(email.starred ? p.star : p.fg4)
                    .frame(width: 30, height: 40)
            }
            .buttonStyle(.plain)
            .help(email.starred ? "Starred" : "Not starred")

            HStack(spacing: 6) {
                if model.isVIP(email.fromEmail) { Icon(name: "crown", size: 10).foregroundStyle(p.star) }
                Text(displayName)
                    .font(.system(size: 13.5, weight: email.unread ? .bold : .regular))
                    .foregroundStyle(p.fg1)
                    .lineLimit(1)
            }
            .frame(width: 190, alignment: .leading)
            .padding(.leading, 6)

            HStack(spacing: 6) {
                (Text(email.subject)
                    .font(.system(size: 13.5, weight: email.unread ? .bold : .regular))
                    .foregroundColor(p.fg1)
                 + Text(email.preview.isEmpty ? "" : " - \(email.preview)")
                    .font(.system(size: 13.5))
                    .foregroundColor(p.fg3))
                    .lineLimit(1)
                Spacer(minLength: 8)
                if email.hasAttachment && !hovered {
                    Icon(name: "attach", size: 13).foregroundStyle(p.fg3)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            if hovered {
                HStack(spacing: 2) {
                    hoverAction("archive", "Archive") { model.archive(email.id) }
                    hoverAction("trash", "Delete") { model.delete(email.id) }
                    hoverAction("mail", email.unread ? "Mark as read" : "Mark as unread") {
                        model.toggleRead(email.id)
                    }
                    hoverAction("clock", "Snooze") { model.snooze(email.id) }
                }
                .padding(.trailing, 8)
            } else {
                Text(email.time)
                    .font(.system(size: 12, weight: email.unread ? .bold : .regular))
                    .monospacedDigit()
                    .foregroundStyle(email.unread ? p.fg1 : p.fg3)
                    .frame(width: 84, alignment: .trailing)
                    .padding(.trailing, 16)
            }
        }
        .padding(.leading, 8)
        .frame(height: 40)
        // Same hover as the sidebar: a soft rounded fill, no shadow, no bold.
        .background(rowBackground)
        .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous)
            .fill(hovered && !bulkSelected ? Arcos.hoverFill(p) : .clear)
            .padding(.horizontal, 6).padding(.vertical, 2)
            .allowsHitTesting(false))
        .overlay(Rectangle().fill(p.border).frame(height: 1), alignment: .bottom)
        .animation(.easeOut(duration: 0.12), value: hovered)
        .contentShape(Rectangle())
        .onTapGesture {
            if model.selectionActive { model.toggleSelect(email.id) } else { model.activate(email.id) }
        }
        .onHover { hovered = $0 }
        .contextMenu { EmailContextMenu(email: email) }
    }

    private var rowBackground: Color {
        // No orange "active" wash on the current row; only checked rows get a neutral fill.
        if bulkSelected { return Arcos.chipFill(p) }
        return email.unread ? p.bg1 : p.readRow
    }

    private func hoverAction(_ icon: String, _ help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Icon(name: icon, size: 15).foregroundStyle(p.fg2).frame(width: 32, height: 32)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
    }
}
