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
        InitializeComponent();
        Palette.Install(Resources);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
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
