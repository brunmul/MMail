using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMail.Core.Agent;

/// A tool failure Claude should see as a readable message.
public sealed class ToolError(string message) : Exception(message);

/// Model Context Protocol over JSON-RPC 2.0, one message per line.
/// Transport-agnostic: <see cref="AgentPipeServer"/> feeds it lines from the local pipe.
public sealed class McpServer(string name, string version, string instructions)
{
    private sealed record Tool(string Name, string Description, JsonObject Schema, Func<JsonObject, Task<JsonArray>> Handler);

    private readonly Dictionary<string, Tool> _tools = [];
    private readonly List<string> _order = [];

    public IReadOnlyList<string> ToolNames => _order;

    public void Register(string toolName, string description, JsonObject? properties, string[]? required,
                         Func<JsonObject, Task<JsonArray>> handler)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties ?? new JsonObject(),
            ["required"] = new JsonArray((required ?? []).Select(r => (JsonNode)r).ToArray()),
        };
        _tools[toolName] = new Tool(toolName, description, schema, handler);
        _order.Add(toolName);
    }

    /// Handles one JSON-RPC line; returns the response line, or null for notifications.
    public async Task<string?> HandleLineAsync(string line)
    {
        JsonObject? request;
        try { request = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { request = null; }
        if (request is null)
            return Error(null, -32700, "Parse error").ToJsonString();
        var response = await HandleAsync(request);
        return response?.ToJsonString();
    }

    private async Task<JsonObject?> HandleAsync(JsonObject request)
    {
        var method = request["method"]?.GetValue<string>() ?? "";
        var parameters = request["params"] as JsonObject ?? [];
        // Notifications carry no id and expect no answer.
        if (!request.TryGetPropertyValue("id", out var id)) return null;

        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = parameters["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = name, ["version"] = version },
                    ["instructions"] = instructions,
                });
            case "ping":
                return Result(id, []);
            case "tools/list":
                var tools = new JsonArray();
                foreach (var t in _order.Select(n => _tools[n]))
                    tools.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone() });
                return Result(id, new JsonObject { ["tools"] = tools });
            case "tools/call":
                var toolName = parameters["name"]?.GetValue<string>() ?? "";
                if (!_tools.TryGetValue(toolName, out var tool)) return Error(id, -32602, $"Unknown tool: {toolName}");
                try
                {
                    var content = await tool.Handler(parameters["arguments"] as JsonObject ?? []);
                    return Result(id, new JsonObject { ["content"] = content });
                }
                catch (Exception ex)
                {
                    return Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(Content.Text(ex.Message)),
                        ["isError"] = true,
                    });
                }
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

public static class Content
{
    public static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    public static JsonObject Json(JsonNode value) =>
        Text(value.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}

public static class Schema
{
    public static JsonObject String(string description) => new() { ["type"] = "string", ["description"] = description };
    public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };
    public static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject OneOf(IEnumerable<string> values, string description) => new()
    {
        ["type"] = "string", ["description"] = description,
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()),
    };

    /// A list of strings; a single comma-separated string is accepted too.
    public static JsonObject Strings(string description) => new()
    {
        ["type"] = "array", ["description"] = description, ["items"] = new JsonObject { ["type"] = "string" },
    };
}

public static class Args
{
    public static string? Str(this JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// The raw string, empty allowed (to clear a field).
    public static string? RawStr(this JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string RequireString(this JsonObject a, string key) =>
        a.Str(key) ?? throw new ToolError($"Missing “{key}”.");

    public static bool? Bool(this JsonObject a, string key) =>
        a[key] is JsonValue v ? v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) ? p : null : null;

    public static int? Int(this JsonObject a, string key) =>
        a[key] is JsonValue v ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d
            : v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null : null;

    /// Recipients as one "a, b" string, from a list or a string.
    public static string? Recipients(this JsonObject a, string key) => a[key] switch
    {
        JsonArray arr => string.Join(", ", arr.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s))),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => null,
    };
}
