using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MMail.App;

/// Our own entry point (the XAML-generated one is turned off in the .csproj) so a failure
/// while WinUI starts, before App exists, still lands in startup.log.
public static class Program
{
    [STAThread]
    private static void Main()
    {
        StartupLog.Begin();
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                StartupLog.Write("WinUI started");
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new App();
            });
        }
        catch (Exception ex)
        {
            App.ReportCrash(ex);
        }
    }
}

/// %LOCALAPPDATA%\MMail\startup.log: the steps of the last launch, and the first exceptions
/// thrown on the way, even those WinUI swallows before failing.
public static class StartupLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private static int _exceptions;

    public static void Begin()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMail");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "startup.log");
            File.WriteAllText(_path, "");
        }
        catch { }
        Write($"MMail {typeof(Program).Assembly.GetName().Version} on {Environment.OSVersion}, {AppContext.BaseDirectory}");
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (Interlocked.Increment(ref _exceptions) <= 40) Write($"exception: {e.Exception}");
        };
    }

    public static void Write(string line)
    {
        if (_path is null) return;
        try { lock (Gate) File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {line}\r\n"); }
        catch { }
    }
}
