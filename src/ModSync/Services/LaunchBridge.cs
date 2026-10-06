using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>The launcher waits for this broker, not for the lifetime of the dashboard.</summary>
public static class LaunchBridge
{
    private static string PipeName => "ModSync-" + PathUtils.InstanceKey(PathUtils.GetAppDirectory()) + "-" + Environment.UserName;

    public static async Task<int> WaitForDecisionAsync(string instance, bool startIfMissing = true)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(500); }
        catch (TimeoutException) when (startIfMissing)
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
            await pipe.ConnectAsync(15000);
        }
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(Path.GetFullPath(instance)));
        return await reader.ReadLineAsync() == "play" ? 0 : 1;
    }

    public static async Task ListenAsync(Func<string, CancellationToken, Task<bool>> decide, Action<bool> replied, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(cancellation); }
            catch { pipe.Dispose(); if (cancellation.IsCancellationRequested) return; throw; }
            _ = ReplyAsync(pipe, decide, replied, cancellation);
        }
    }

    private static async Task ReplyAsync(NamedPipeServerStream pipe, Func<string, CancellationToken, Task<bool>> decide, Action<bool> replied, CancellationToken cancellation)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await reader.ReadLineAsync(timeout.Token);
                string? instance = JsonSerializer.Deserialize<string>(line ?? "null");
                if (string.IsNullOrWhiteSpace(instance)) return;
                using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var decision = decide(instance, session.Token);
                // The broker sends one request, then only reads. EOF here means it went away.
                var disconnected = reader.ReadLineAsync(session.Token).AsTask();
                if (await Task.WhenAny(decision, disconnected) != decision)
                {
                    session.Cancel();
                    return;
                }
                bool play = await decision;
                await writer.WriteLineAsync(play ? "play" : "cancel");
                await writer.FlushAsync(cancellation);
                session.Cancel();
                replied(play);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
        }
    }
}
