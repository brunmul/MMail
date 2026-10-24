using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MMail.Core.Agent;
using MMail.Core.Mail;
using MMail.Core.Platform;
using MMail.Core.State;
using MMail.Core.Storage;

namespace MMail.App;

public partial class App : Application
{
    private MainWindow? _window;
    private AgentPipeServer? _agent;
    private static Mutex? _singleInstance;

    public App()
    {
        // A crash at startup would otherwise close MMail without a word: say what happened.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);
        UnhandledException += (_, e) => ReportCrash(e.Exception);
        InitializeComponent();
        Palette.Install(Resources);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { Launch(); }
        catch (Exception ex)
        {
            ReportCrash(ex);
            Exit();
        }
    }

    private void Launch()
    {
        // One MMail per user: the second launch (e.g. from mmail-mcp) just quits.
        _singleInstance = new Mutex(true, $"MMail-{Environment.UserName}", out var first);
        if (!first)
        {
            Exit();
            return;
        }

        // WebView2 keeps its profile with our data, not next to the exe (which may be read-only).
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(AppPaths.Home, "WebView2"));

        var store = MailStore.OpenDefault();
        var credentials = new WindowsCredentialStore();
        var model = new MailController(store, new MailKitService(credentials), credentials);

        _window = new MainWindow(model, store);
        _agent = new AgentPipeServer(AgentTools.CreateServer(model), new WinUiDispatcher(_window.DispatcherQueue), AppPaths.AgentPipeName);
        _agent.Start();

        var background = Environment.GetCommandLineArgs().Contains("--background");
        _window.Activate();
        if (background) _window.Minimize();
    }

    private static int _reported;

    /// Writes the error to %LOCALAPPDATA%\MMail\crash.log and shows it in a plain Win32 box,
    /// which works even when XAML itself is what failed.
    public static void ReportCrash(Exception? ex)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 1) return;
        var text = ex?.ToString() ?? "Unknown error";
        var log = Path.Combine(AppPaths.Home, "crash.log");
        try
        {
            Directory.CreateDirectory(AppPaths.Home);
            File.AppendAllText(log, $"--- {DateTime.Now:O} MMail {typeof(App).Assembly.GetName().Version}\r\n{text}\r\n");
        }
        catch { }
        MessageBoxW(IntPtr.Zero, $"MMail stopped because of an error.\n\n{ex?.Message}\n\nDetails were saved to {log}", "MMail", 0x10);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

/// Runs the agent's requests on the window's thread, where the controller lives.
public sealed class WinUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = queue.TryEnqueue(async () =>
        {
            try { done.SetResult(await work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        if (!queued) done.SetException(new InvalidOperationException("MMail is closing."));
        return done.Task;
    }
}
