using System.Diagnostics;
using System.IO.Pipes;

// mmail-mcp: relays Claude's stdin/stdout to the running MMail app. It does no mail
// work itself; the app answers, so whatever Claude does shows up in the window.
var pipeName = $"mmail-agent-{Environment.UserName}";

var pipe = await Connect(TimeSpan.FromMilliseconds(500));
if (pipe is null)
{
    LaunchApp();
    pipe = await Connect(TimeSpan.FromSeconds(20));
}
if (pipe is null)
{
    Console.Error.WriteLine("MMail is not running and could not be started.");
    return 1;
}

using (pipe)
{
    var stdin = Console.OpenStandardInput();
    var stdout = Console.OpenStandardOutput();
    // App → Claude. When the app goes away, so do we.
    var down = Task.Run(async () => { try { await pipe.CopyToAsync(stdout); } catch (IOException) { } });
    // Claude → app.
    var up = Task.Run(async () => { try { await stdin.CopyToAsync(pipe); } catch (IOException) { } });
    var first = await Task.WhenAny(down, up);
    if (first == up)
    {
        // Claude closed stdin: let the last answers drain before quitting.
        await Task.WhenAny(down, Task.Delay(TimeSpan.FromSeconds(2)));
    }
}
return 0;

async Task<NamedPipeClientStream?> Connect(TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (true)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                                               PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var left = deadline - DateTime.UtcNow;
            await client.ConnectAsync((int)Math.Max(100, Math.Min(left.TotalMilliseconds, 1000)));
            return client;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            client.Dispose();
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(250);
        }
    }
}

static void LaunchApp()
{
    // MMail.exe sits next to mmail-mcp.exe; --background keeps it minimized.
    var app = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "MMail.exe" : "MMail");
    if (!File.Exists(app)) return;
    try
    {
        Process.Start(new ProcessStartInfo(app, "--background") { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not start MMail: {ex.Message}");
    }
}
