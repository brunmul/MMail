import Foundation
import NIOCore
import NIOPosix

/// Local endpoint Claude talks to. The running app listens on a Unix socket
/// readable only by the current user; `MMail --mcp` (see `MCPProxy`) bridges
/// Claude's stdio to it. Everything goes through `AppModel`, so whatever
/// Claude does shows up in the window and benefits from the same IMAP/SMTP
/// sessions, caches, toasts and Undo.
final class AgentServer {
    static let shared = AgentServer()

    static var socketURL: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("MMail", isDirectory: true).appendingPathComponent("agent.sock")
    }

    private var channel: Channel?
    private var server: MCPServer?

    @MainActor
    func start(model: AppModel) {
        guard server == nil else { return }
        let mcp = AgentTools.makeServer(model: model)
        server = mcp
        let path = Self.socketURL.path
        try? FileManager.default.createDirectory(at: Self.socketURL.deletingLastPathComponent(),
                                                 withIntermediateDirectories: true)
        unlink(path)   // stale socket from a previous run
        let bootstrap = ServerBootstrap(group: MultiThreadedEventLoopGroup.singleton)
            .serverChannelOption(ChannelOptions.backlog, value: 8)
            .childChannelInitializer { channel in
                channel.pipeline.addHandler(AgentLineHandler(server: mcp))
            }
        Task {
            do {
                let ch = try await bootstrap.bind(unixDomainSocketPath: path).get()
                chmod(path, 0o600)
                await MainActor.run { self.channel = ch }
            } catch {
                NSLog("MMail agent socket failed: \(error)")
            }
        }
    }
}

/// Splits the byte stream into lines and answers each JSON-RPC request.
private final class AgentLineHandler: ChannelInboundHandler {
    typealias InboundIn = ByteBuffer
    typealias OutboundOut = ByteBuffer

    private let server: MCPServer
    private var pending = Data()

    init(server: MCPServer) { self.server = server }

    func channelRead(context: ChannelHandlerContext, data: NIOAny) {
        var buffer = unwrapInboundIn(data)
        if let bytes = buffer.readBytes(length: buffer.readableBytes) { pending.append(contentsOf: bytes) }
        let channel = context.channel
        while let newline = pending.firstIndex(of: 0x0A) {
            let line = String(decoding: pending[pending.startIndex..<newline], as: UTF8.self)
            pending.removeSubrange(pending.startIndex...newline)
            guard !line.trimmingCharacters(in: .whitespaces).isEmpty else { continue }
            let server = self.server
            Task {
                guard let reply = await server.handle(line: line) else { return }
                var out = channel.allocator.buffer(capacity: reply.utf8.count + 1)
                out.writeString(reply)
                out.writeString("\n")
                channel.writeAndFlush(out, promise: nil)
            }
        }
    }

    func errorCaught(context: ChannelHandlerContext, error: Error) {
        context.close(promise: nil)
    }
}
