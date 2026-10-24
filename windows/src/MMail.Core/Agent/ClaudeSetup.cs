using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMail.Core.Agent;

/// Connects Claude to MMail: adds the "mmail" MCP server (mmail-mcp.exe) to Claude
/// Desktop's configuration, keeping everything else in the file.
public static class ClaudeSetup
{
    public const string ServerName = "mmail";

    /// Where Claude Desktop keeps its configuration: %APPDATA%\Claude, and the
    /// Microsoft Store install's own copy when it exists.
    public static IReadOnlyList<string> DesktopConfigPaths()
    {
        var paths = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json"),
        };
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var dir in Directory.EnumerateDirectories(packages, "Claude_*"))
            {
                var roaming = Path.Combine(dir, "LocalCache", "Roaming", "Claude");
                if (Directory.Exists(roaming)) paths.Add(Path.Combine(roaming, "claude_desktop_config.json"));
            }
        }
        return paths;
    }

    /// Adds or updates the mmail server in one config file. Returns false when the
    /// file exists but is not valid JSON (left untouched).
    public static bool AddToConfig(string configPath, string bridgePath)
    {
        JsonObject root;
        if (File.Exists(configPath))
        {
            try { root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? []; }
            catch (JsonException) { return false; }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            root = [];
        }
        if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = [];
        servers[ServerName] = new JsonObject { ["command"] = bridgePath, ["args"] = new JsonArray() };
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    /// The command that registers MMail with Claude Code.
    public static string ClaudeCodeCommand(string bridgePath) => $"claude mcp add {ServerName} --scope user -- \"{bridgePath}\"";

    public static string ConfigSnippet(string bridgePath) => new JsonObject
    {
        ["mcpServers"] = new JsonObject { [ServerName] = new JsonObject { ["command"] = bridgePath } },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
