namespace MMail.Core.Storage;

/// Where MMail keeps its data: %LOCALAPPDATA%\MMail, or MMAIL_HOME when set
/// (tests and side-by-side installs).
public static class AppPaths
{
    public static string Home
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("MMAIL_HOME");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMail");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Database => Path.Combine(Home, "mmail.db");

    /// The local pipe Claude's bridge (mmail-mcp.exe) connects to. Per user.
    public static string AgentPipeName => $"mmail-agent-{Environment.UserName}";
}
