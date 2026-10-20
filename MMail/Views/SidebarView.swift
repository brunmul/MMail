import AppKit
import SwiftUI

struct SidebarView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.palette) private var p
    @State private var hoveredFolder: String?

    private let folderIcons: [String: String] = [
        "home": "home", "inbox": "inbox", "starred": "star", "snoozed": "clock",
        "done": "done", "archive": "archive", "sent": "send", "outbox": "outbox", "drafts": "draft", "spam": "spam", "trash": "trash"
    ]

    @State private var labelsOpen = true
    @State private var now = Date()
    private let tick = Timer.publish(every: 30, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            welcome
                .padding(.horizontal, 8)
                .padding(.top, 6)
                .padding(.bottom, 12)

            composeButton
                .padding(.bottom, 6)

            WaveSeparator()

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

            if model.folder != "home" && !model.labels.isEmpty {
                WaveSeparator()
                groupHeader("Labels", count: model.labels.count, open: $labelsOpen)
                if labelsOpen {
                    VStack(spacing: 1) {
                        ForEach(model.labels) { l in
                            labelRow(l)
                        }
                    }
                }
            }

            Spacer(minLength: 8)

            WaveSeparator()
            footer
        }
        .padding(8)
        .frame(width: 216)
        .frame(maxHeight: .infinity)
        .background(p.bg2)
        .onReceive(tick) { now = $0 }
    }

    // MARK: - Welcome (the only serif line in the bar)

    private var welcome: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(greeting)
                .font(Typo.serif(19))
                .foregroundStyle(p.fg1)
                .lineLimit(1)
            HStack {
                Text(now.formatted(.dateTime.weekday(.wide).day().month(.wide)))
                Spacer()
                Text(now.formatted(date: .omitted, time: .shortened))
                    .fontWeight(.medium)
                    .monospacedDigit()
            }
            .font(.system(size: 12))
            .foregroundStyle(p.fg2)
        }
    }

    private var greeting: String {
        let hour = Calendar.current.component(.hour, from: now)
        let hello = hour < 5 ? "Good night" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening"
        let first = NSFullUserName().split(separator: " ").first.map(String.init) ?? ""
        return first.isEmpty ? hello : "\(hello), \(first)"
    }

    // MARK: - Compose: the white "current" pill with a touch of clay

    private var composeButton: some View {
        Button { model.startCompose() } label: {
            HStack(spacing: 8) {
                Icon(name: "pencil", size: 14, weight: .regular)
                    .foregroundStyle(Arcos.clay)
                Text("Compose").font(.system(size: 13, weight: .medium))
                Spacer()
                Text("C")
                    .font(.system(size: 10, weight: .medium))
                    .foregroundStyle(p.fg3)
                    .padding(.horizontal, 5)
                    .frame(height: 15)
                    .background(Arcos.chipFill(p), in: RoundedRectangle(cornerRadius: 5, style: .continuous))
            }
            .foregroundStyle(p.composeFg)
            .padding(.leading, 8)
            .padding(.trailing, 6)
            .frame(height: 32)
            .background(p.composeBg, in: RoundedRectangle(cornerRadius: 7, style: .continuous))
            .shadow(color: Arcos.currentShadow, radius: 1.5, y: 0.5)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Compose (C)")
    }

    // MARK: - Rows

    private func row<Leading: View>(active: Bool, hovered: Bool, @ViewBuilder content: () -> Leading) -> some View {
        content()
            .padding(.leading, 8)
            .padding(.trailing, 6)
            .frame(height: 28)
            .frame(maxWidth: .infinity)
            .background(
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(active ? Arcos.current(p) : (hovered ? Arcos.hoverFill(p) : Color.clear))
                    .shadow(color: active ? Arcos.currentShadow : .clear, radius: 1.5, y: 0.5)
            )
            .contentShape(Rectangle())
            .animation(.easeOut(duration: 0.12), value: hovered)
    }

    private func folderRow(_ f: Folder) -> some View {
        let active = model.folder == f.id && model.labelFilter == nil
        let hovered = hoveredFolder == f.id
        let count = f.id == "outbox" ? (model.scheduled.count + model.sending.count) : (model.unreadCounts[f.id] ?? 0)
        return Button { model.setFolder(f.id) } label: {
            row(active: active, hovered: hovered) {
                HStack(spacing: 8) {
                    Icon(name: folderIcons[f.id] ?? "mail", size: 14)
                        .foregroundStyle(active ? p.fg1 : p.fg2)
                        .frame(width: 16)
                    Text(f.name)
                        .font(.system(size: 13, weight: count > 0 ? .medium : .regular))
                        .foregroundStyle(p.fg1)
                        .lineLimit(1)
                    Spacer(minLength: 4)
                    if count > 0 {
                        Text("\(count)")
                            .font(.system(size: 11))
                            .monospacedDigit()
                            .foregroundStyle(p.fg2)
                    }
                }
            }
        }
        .buttonStyle(.plain)
        .onHover { hoveredFolder = $0 ? f.id : (hoveredFolder == f.id ? nil : hoveredFolder) }
    }

    private func labelRow(_ l: MailLabel) -> some View {
        let active = model.labelFilter == l.id
        let hovered = hoveredFolder == "label:\(l.id)"
        return Button { model.selectLabel(l.id) } label: {
            row(active: active, hovered: hovered) {
                HStack(spacing: 8) {
                    // Arcos folder tile: a light veil of the colour behind an outline symbol.
                    Image(systemName: "tag")
                        .font(.system(size: 9, weight: .light))
                        .foregroundStyle(l.color)
                        .frame(width: 16, height: 16)
                        .background(l.color.opacity(p.isDark ? 0.2 : 0.12),
                                    in: RoundedRectangle(cornerRadius: 4, style: .continuous))
                    Text(l.name)
                        .font(.system(size: 13))
                        .foregroundStyle(p.fg1)
                        .lineLimit(1)
                    Spacer(minLength: 0)
                }
            }
        }
        .buttonStyle(.plain)
        .onHover { hoveredFolder = $0 ? "label:\(l.id)" : (hoveredFolder == "label:\(l.id)" ? nil : hoveredFolder) }
    }

    private func groupHeader(_ title: String, count: Int, open: Binding<Bool>) -> some View {
        Button {
            withAnimation(.spring(response: 0.28, dampingFraction: 0.86)) { open.wrappedValue.toggle() }
        } label: {
            HStack(spacing: 5) {
                Image(systemName: "chevron.right")
                    .font(.system(size: 8, weight: .bold))
                    .foregroundStyle(p.fg4)
                    .rotationEffect(.degrees(open.wrappedValue ? 90 : 0))
                Text(title)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(p.fg2)
                if !open.wrappedValue {
                    Text("\(count)")
                        .font(.system(size: 10, weight: .medium))
                        .monospacedDigit()
                        .foregroundStyle(p.fg2)
                        .padding(.horizontal, 5)
                        .frame(height: 15)
                        .background(Arcos.chipFill(p), in: Capsule())
                }
                Spacer()
            }
            .padding(.leading, 8)
            .frame(height: 22)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private var footer: some View {
        let isAll = model.currentAccount == "all"
        let acct = model.accountsById[model.currentAccount]
        return HStack(spacing: 8) {
            HStack(spacing: 10) {
                GradientTile(colors: isAll ? [Color(hex: "D97757"), Color(hex: "C15F3C")] : (acct?.gradientColors ?? []),
                             text: isAll ? "M" : (acct?.initials ?? "M"),
                             size: 24, cornerRadius: 7, fontSize: 10)
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
        .padding(.horizontal, 8)
        .padding(.top, 2)
    }
}
