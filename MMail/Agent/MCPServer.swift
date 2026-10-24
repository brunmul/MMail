import Foundation

typealias JSON = [String: Any]

/// Model Context Protocol over JSON-RPC 2.0, one message per line.
/// Transport-agnostic: `AgentServer` feeds it lines from the local socket.
final class MCPServer {
    struct Tool {
        let name: String
        let description: String
        let schema: JSON
        let handler: (JSON) async throws -> [JSON]
    }

    struct ToolError: LocalizedError {
        let message: String
        init(_ message: String) { self.message = message }
        var errorDescription: String? { message }
    }

    private var tools: [String: Tool] = [:]
    private var order: [String] = []
    let name: String
    let version: String
    let instructions: String

    init(name: String, version: String, instructions: String) {
        self.name = name
        self.version = version
        self.instructions = instructions
    }

    func register(_ name: String, _ description: String, properties: JSON = [:], required: [String] = [],
                  handler: @escaping (JSON) async throws -> [JSON]) {
        let schema: JSON = ["type": "object", "properties": properties, "required": required]
        tools[name] = Tool(name: name, description: description, schema: schema, handler: handler)
        order.append(name)
    }

    /// Handles one JSON-RPC line; returns the response line, or nil for notifications.
    func handle(line: String) async -> String? {
        guard let data = line.data(using: .utf8),
              let request = try? JSONSerialization.jsonObject(with: data) as? JSON else {
            return encode(["jsonrpc": "2.0", "id": NSNull(), "error": ["code": -32700, "message": "Parse error"]])
        }
        guard let response = await handle(request) else { return nil }
        return encode(response)
    }

    private func handle(_ request: JSON) async -> JSON? {
        let method = request["method"] as? String ?? ""
        let params = request["params"] as? JSON ?? [:]
        // Notifications carry no id and expect no answer.
        guard let id = request["id"] else { return nil }

        func result(_ r: JSON) -> JSON { ["jsonrpc": "2.0", "id": id, "result": r] }
        func failure(_ code: Int, _ message: String) -> JSON {
            ["jsonrpc": "2.0", "id": id, "error": ["code": code, "message": message]]
        }

        switch method {
        case "initialize":
            return result([
                "protocolVersion": params["protocolVersion"] as? String ?? "2025-06-18",
                "capabilities": ["tools": ["listChanged": false]],
                "serverInfo": ["name": name, "version": version],
                "instructions": instructions,
            ])
        case "ping":
            return result([:])
        case "tools/list":
            return result(["tools": order.compactMap { tools[$0] }.map {
                ["name": $0.name, "description": $0.description, "inputSchema": $0.schema] as JSON
            }])
        case "tools/call":
            guard let toolName = params["name"] as? String, let tool = tools[toolName] else {
                return failure(-32602, "Unknown tool: \(params["name"] as? String ?? "")")
            }
            do {
                let content = try await tool.handler(params["arguments"] as? JSON ?? [:])
                return result(["content": content, "isError": false])
            } catch {
                return result(["content": [["type": "text", "text": "Error: \(error.localizedDescription)"]], "isError": true])
            }
        default:
            return failure(-32601, "Method not found: \(method)")
        }
    }

    private func encode(_ message: JSON) -> String? {
        guard let data = try? JSONSerialization.data(withJSONObject: message, options: [.withoutEscapingSlashes]) else { return nil }
        return String(decoding: data, as: UTF8.self)
    }
}

// MARK: - Argument helpers

extension Dictionary where Key == String, Value == Any {
    func string(_ key: String) -> String? {
        guard let v = self[key] as? String, !v.isEmpty else { return nil }
        return v
    }

    func requireString(_ key: String) throws -> String {
        guard let v = string(key) else { throw MCPServer.ToolError("Missing parameter “\(key)”.") }
        return v
    }

    func int(_ key: String) -> Int? {
        if let v = self[key] as? Int { return v }
        if let v = self[key] as? Double { return Int(v) }
        if let v = self[key] as? String { return Int(v) }
        return nil
    }

    func bool(_ key: String) -> Bool? { self[key] as? Bool }

    /// Accepts a JSON list or a comma-separated string; returns a comma-separated field.
    func recipients(_ key: String) -> String? {
        if let v = self[key] as? [String] { return v.joined(separator: ", ") }
        return self[key] as? String
    }
}

enum Schema {
    static func string(_ d: String) -> JSON { ["type": "string", "description": d] }
    static func integer(_ d: String) -> JSON { ["type": "integer", "description": d] }
    static func boolean(_ d: String) -> JSON { ["type": "boolean", "description": d] }
    static func strings(_ d: String) -> JSON { ["type": "array", "items": ["type": "string"], "description": d] }
    static func oneOf(_ values: [String], _ d: String) -> JSON { ["type": "string", "enum": values, "description": d] }
}

func textContent(_ s: String) -> JSON { ["type": "text", "text": s] }

func jsonContent(_ value: Any) -> JSON {
    let data = (try? JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])) ?? Data()
    return textContent(String(decoding: data, as: UTF8.self))
}
