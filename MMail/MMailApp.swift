import SwiftUI

@main
enum Entry {
    static func main() {
        // `MMail --mcp` is the MCP server entry point Claude launches (no UI).
        if CommandLine.arguments.contains("--mcp") { MCPProxy.run() }
        MMailApp.main()
    }
}

struct MMailApp: App {
    @StateObject private var model = AppModel()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(model)
                .frame(minWidth: 920, minHeight: 600)
                .preferredColorScheme(model.dark ? .dark : .light)
                .environment(\.palette, model.dark ? .dark : .light)
                .task { AgentServer.shared.start(model: model) }
        }
        .windowStyle(.titleBar)
        .windowToolbarStyle(.unified)
        .defaultSize(width: 1340, height: 860)
        .commands {
            CommandGroup(replacing: .help) {
                Button("MMail Keyboard Shortcuts") { model.help = true }
                    .keyboardShortcut("?", modifiers: [])
            }
        }
    }
}
