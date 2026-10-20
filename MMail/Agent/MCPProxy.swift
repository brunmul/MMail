import Foundation

/// `MMail --mcp`: what Claude launches as its MCP server. It does no mail work
/// itself; it relays stdin/stdout to the running app's agent socket, starting
/// the app in the background first if needed.
enum MCPProxy {
    static func run() -> Never {
        let path = AgentServer.socketURL.path
        var fd = connect(path)
        if fd < 0 {
            launchApp()
            for _ in 0..<60 where fd < 0 {
                usleep(250_000)
                fd = connect(path)
            }
        }
        guard fd >= 0 else {
            FileHandle.standardError.write(Data("MMail is not running and could not be started.\n".utf8))
            exit(1)
        }
        let socketFD = fd

        // App → Claude
        Thread.detachNewThread {
            pump(from: socketFD, to: STDOUT_FILENO)
            exit(0)
        }
        // Claude → app
        pump(from: STDIN_FILENO, to: socketFD)
        shutdown(socketFD, SHUT_WR)
        // Let the last answers drain before quitting.
        sleep(2)
        exit(0)
    }

    private static func connect(_ path: String) -> Int32 {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return -1 }
        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let capacity = MemoryLayout.size(ofValue: addr.sun_path)
        guard path.utf8.count < capacity else { close(fd); return -1 }
        withUnsafeMutablePointer(to: &addr.sun_path) { ptr in
            ptr.withMemoryRebound(to: CChar.self, capacity: capacity) { _ = strncpy($0, path, capacity - 1) }
        }
        let result = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        if result != 0 { close(fd); return -1 }
        return fd
    }

    private static func launchApp() {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        task.arguments = ["-g", "-a", Bundle.main.bundleURL.path]   // -g: stay in the background
        try? task.run()
    }

    private static func pump(from source: Int32, to destination: Int32) {
        var buffer = [UInt8](repeating: 0, count: 64 * 1024)
        while true {
            let n = read(source, &buffer, buffer.count)
            if n <= 0 { return }
            var offset = 0
            while offset < n {
                let written = buffer[offset..<n].withUnsafeBytes { write(destination, $0.baseAddress, $0.count) }
                if written <= 0 { return }
                offset += written
            }
        }
    }
}
