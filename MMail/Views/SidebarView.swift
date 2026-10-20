import SwiftUI

struct SidebarView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.palette) private var p
    @State private var hoveredFolder: String?

    private let folderIcons: [String: String] = [
        "home": "home", "inbox": "inbox", "starred": "star", "snoozed": "clock",
        "done": "done", "archive": "archive", "sent": "send", "outbox": "outbox", "drafts": "draft", "spam": "spam", "trash": "trash"
    ]

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            composeButton
                .padding(.leading, 8)
                .padding(.top, 4)
                .padding(.bottom, 16)

            VStack(spacing: 1) {
                // Home is the cross-account dashboard (weather, contacts,
                // journal, todos), so it only makes sense in the unified
                // "All inboxes" scope. Hide it when a specific account is
                // selected.
                ForEach(SampleData.folders.filter { f in
                    f.id != "home" || model.currentAccount == "all"
                }) { f in
                    folderRow(f)
                }
            }

            if model.folder != "home" {
                Text("Labels")
                    .font(.system(size: 16, weight: .medium))
                    .foregroundStyle(p.fg1)
                    .padding(.leading, 26)
                    .padding(.top, 18)
                    .padding(.bottom, 6)

                ForEach(model.labels) { l in
                    labelRow(l)
                }
            }

            Spacer(minLength: 8)

            footer
        }
        .padding(.leading, 0)
        .padding(.trailing, 12)
        .padding(.vertical, 8)
        .frame(width: 256)
        .frame(maxHeight: .infinity)
        .background(p.bg2)
    }

    private var composeButton: some View {
        Button { model.startCompose() } label: {
            HStack(spacing: 12) {
                Icon(name: "pencil", size: 18)
                Text("Compose").font(.system(size: 14, weight: .semibold))
            }
            .foregroundStyle(p.composeFg)
            .padding(.leading, 18)
            .padding(.trailing, 24)
            .frame(height: 56)
            .background(p.composeBg)
            .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        }
        .buttonStyle(.plain)
        .help("Compose (C)")
    }

    private func folderRow(_ f: Folder) -> some View {
        let active = model.folder == f.id
        let hovered = hoveredFolder == f.id
        let count = f.id == "outbox" ? (model.scheduled.count + model.sending.count) : (model.unreadCounts[f.id] ?? 0)
        return Button { model.setFolder(f.id) } label: {
            HStack(spacing: 18) {
                Icon(name: folderIcons[f.id] ?? "mail", size: 17)
                Text(f.name).font(.system(size: 14, weight: active || count > 0 ? .bold : .regular))
                Spacer()
                if count > 0 {
                    Text("\(count)")
                        .font(.system(size: 12, weight: .bold))
                        .monospacedDigit()
                }
            }
            .foregroundStyle(active ? p.activeFolderText : p.fg1)
            .padding(.leading, 26)
            .padding(.trailing, 16)
            .frame(height: 32)
            .frame(maxWidth: .infinity)
            .background(active ? p.brandBlue100 : (hovered ? p.bg4.opacity(0.7) : Color.clear))
            .clipShape(UnevenRoundedRectangle(bottomTrailingRadius: 16, topTrailingRadius: 16, style: .continuous))
        }
        .buttonStyle(.plain)
        .onHover { hoveredFolder = $0 ? f.id : (hoveredFolder == f.id ? nil : hoveredFolder) }
    }

    private func labelRow(_ l: MailLabel) -> some View {
        let active = model.labelFilter == l.id
        let hovered = hoveredFolder == "label:\(l.id)"
        return Button { model.selectLabel(l.id) } label: {
            HStack(spacing: 18) {
                Image(systemName: "tag.fill").font(.system(size: 13)).foregroundStyle(l.color)
                Text(l.name).font(.system(size: 14, weight: active ? .bold : .regular))
                    .foregroundStyle(active ? p.activeFolderText : p.fg1).lineLimit(1)
                Spacer()
            }
            .padding(.leading, 28)
            .padding(.trailing, 16)
            .frame(height: 32)
            .frame(maxWidth: .infinity)
            .background(active ? p.brandBlue100 : (hovered ? p.bg4.opacity(0.7) : Color.clear))
            .clipShape(UnevenRoundedRectangle(bottomTrailingRadius: 16, topTrailingRadius: 16, style: .continuous))
        }
        .buttonStyle(.plain)
        .onHover { hoveredFolder = $0 ? "label:\(l.id)" : (hoveredFolder == "label:\(l.id)" ? nil : hoveredFolder) }
    }

    private var footer: some View {
        let isAll = model.currentAccount == "all"
        let acct = model.accountsById[model.currentAccount]
        return HStack(spacing: 8) {
            HStack(spacing: 10) {
                GradientTile(colors: isAll ? [Color(hex: "2D3DEC"), Color(hex: "7A5AE0")] : (acct?.gradientColors ?? []),
                             text: isAll ? "M" : (acct?.initials ?? "M"),
                             size: 28, cornerRadius: 14, fontSize: 11)
                VStack(alignment: .leading, spacing: 0) {
                    Text(isAll ? "All inboxes" : (acct?.name ?? "")).font(.system(size: 12.5, weight: .semibold)).foregroundStyle(p.fg1)
                    Text(isAll ? "Unified view" : (acct?.email ?? "")).font(.system(size: 11)).foregroundStyle(p.fg3)
                        .lineLimit(1)
                }
                Spacer(minLength: 0)
            }

            Button { model.help = true } label: { Icon(name: "command", size: 14).foregroundStyle(p.fg2) }
                .buttonStyle(.plain).help("Shortcuts (?)")
            Button { model.settings = true } label: { Icon(name: "settings", size: 14).foregroundStyle(p.fg2) }
                .buttonStyle(.plain).help("Settings")
        }
        .padding(.leading, 20)
        .padding(.top, 8)
    }
}
