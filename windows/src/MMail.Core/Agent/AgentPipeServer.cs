using System.IO.Pipes;
using System.Text;

namespace MMail.Core.Agent;

/// Runs work on the UI thread (the window's dispatcher). Tests run it inline.
public interface IUiDispatcher
{
    Task<T> RunAsync<T>(Func<Task<T>> work);
}

public sealed class InlineDispatcher : IUiDispatcher
{
    public Task<T> RunAsync<T>(Func<Task<T>> work) => work();
}

/// Local endpoint Claude talks to: a named pipe only the current user can open.
/// mmail-mcp.exe bridges Claude's stdio to it. Each JSON-RPC line is answered on
/// the UI thread, so tools act on the same state the window shows.
public sealed class AgentPipeServer(McpServer server, IUiDispatcher dispatcher, string pipeName) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    public void Start() => _ = Task.Run(AcceptLoop);

    public void Dispose() => _stop.Cancel();

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                                 PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(1000); continue; }
            _ = Task.Run(() => Serve(pipe));
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var writeGate = new SemaphoreSlim(1, 1);
            var inFlight = new List<Task>();
            try
            {
                while (!_stop.IsCancellationRequested && await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (line.Trim().Length == 0) continue;
                    // Requests run concurrently (a slow fetch must not hold up a ping).
                    inFlight.Add(Task.Run(async () =>
                    {
                        var reply = await dispatcher.RunAsync(() => server.HandleLineAsync(line));
                        if (reply is null) return;
                        await writeGate.WaitAsync();
                        try { await writer.WriteLineAsync(reply); }
                        finally { writeGate.Release(); }
                    }));
                    inFlight.RemoveAll(t => t.IsCompleted);
                }
                await Task.WhenAll(inFlight);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
}
