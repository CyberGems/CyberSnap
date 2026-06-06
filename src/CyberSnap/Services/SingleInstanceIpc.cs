using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace CyberSnap.Services;

public static class SingleInstanceIpcServer
{
    private const string PipeName = "CyberSnap_SingleInstance_IPC";
    private static CancellationTokenSource? _cts;

    public static void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var args = line.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                    if (System.Windows.Application.Current is CyberSnap.App app)
                    {
                        _ = app.Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                app.HandleCommandLineArgs(args);
                            }
                            catch (Exception ex)
                            {
                                AppDiagnostics.LogError("ipc.server.handle-args", ex);
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppDiagnostics.LogError("ipc.server.loop", ex);
                    await Task.Delay(1000, token).ConfigureAwait(false); // Brief backoff on error
                }
            }
        }, token);
    }

    public static void Stop()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }
    }
}

public static class SingleInstanceIpcClient
{
    private const string PipeName = "CyberSnap_SingleInstance_IPC";

    public static void SendArgs(string[] args)
    {
        if (args == null || args.Length == 0) return;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000); // 3-second timeout

            using var writer = new StreamWriter(client, Encoding.UTF8);
            var content = string.Join("\n", args);
            writer.Write(content);
            writer.Flush();
        }
        catch (Exception ex)
        {
            // Just log locally - we don't want secondary instance crash to impact user experience
            Debug.WriteLine($"Failed to send args to primary instance: {ex.Message}");
        }
    }
}

