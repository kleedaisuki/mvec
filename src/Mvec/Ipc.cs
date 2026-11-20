using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Mvec;

internal static class Ipc
{
    internal const string Reloading = "daemon_reloading";

    public static async Task<Reply> CallAsync(Project project, Request request, CancellationToken ct)
    {
        for (var retries = 0; ; retries++)
        {
            var reply = await ExchangeAsync(project, request, ct);
            // Only retry an explicit rejection before dispatch, never a lost reply.
            if (reply.Error?.Code != Reloading || retries == 3)
                return reply;
            await Task.Delay(40, ct);
        }
    }

    private static async Task<Reply> ExchangeAsync(Project project, Request request, CancellationToken ct)
    {
        await using var stream = await DaemonClient.ConnectAsync(project, ct);
        using var output = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var input = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await output.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Default.Request).AsMemory(), ct);
        var line = await input.ReadLineAsync(ct) ?? throw new IOException("Daemon disconnected without a reply.");
        return JsonSerializer.Deserialize(line, Wire.Default.Reply) ?? throw new IOException("Daemon sent an empty reply.");
    }

    internal static async Task<Stream?> TryConnectAsync(Project project, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", project.Pipe, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { pipe.Connect(1); return pipe; }
            catch (TimeoutException) { pipe.Dispose(); return null; }
            catch { pipe.Dispose(); throw; }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(project.Socket), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.ConnectionRefused)
        {
            socket.Dispose();
            return null;
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new IOException($"Cannot connect to {project.Socket}: {ex.Message}", ex);
        }
        catch { socket.Dispose(); throw; }
    }

    public static async Task ServeAsync(Project project, Func<Request, CancellationToken, Task<Reply>> dispatch,
        TimeSpan idleTimeout, CancellationToken ct, CancellationToken reload)
    {
        using var accepting = CancellationTokenSource.CreateLinkedTokenSource(ct, reload);
        await using var idle = new IdleDeadline(idleTimeout, accepting);
        var clients = new ConcurrentDictionary<Task, byte>();
        try
        {
            if (OperatingSystem.IsWindows())
                await AcceptPipesAsync();
            else
                await AcceptSocketsAsync();
        }
        catch (OperationCanceledException) when (accepting.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients.Keys); }

        async Task AcceptPipesAsync()
        {
            while (!accepting.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(project.Pipe, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(accepting.Token); }
                catch { pipe.Dispose(); throw; }
                Add(pipe);
            }
        }

        async Task AcceptSocketsAsync()
        {
            var endpoint = new UnixDomainSocketEndPoint(project.Socket);
            File.Delete(project.Socket);
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(endpoint);
            try
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(project.Socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                socket.Listen(128);
                while (!accepting.IsCancellationRequested)
                    Add(new NetworkStream(await socket.AcceptAsync(accepting.Token), ownsSocket: true));
            }
            catch (OperationCanceledException) when (accepting.IsCancellationRequested) { }
            finally { File.Delete(project.Socket); }

            // Unlink before draining the backlog so clients already connected can finish.
            while (!ct.IsCancellationRequested && socket.Poll(0, SelectMode.SelectRead))
                Add(new NetworkStream(socket.Accept(), ownsSocket: true));
        }

        void Add(Stream stream)
        {
            idle.Enter();
            var handler = Task.Run(async () =>
            {
                try { await HandleAsync(stream, dispatch, ct); }
                finally { idle.Leave(); }
            });
            clients.TryAdd(handler, 0);
            _ = RemoveAsync(handler);
        }

        async Task RemoveAsync(Task task)
        {
            try { await task; }
            finally { clients.TryRemove(task, out _); }
        }
    }

    private static async Task HandleAsync(Stream stream, Func<Request, CancellationToken, Task<Reply>> dispatch, CancellationToken ct)
    {
        await using (stream)
        {
            try
            {
                using var input = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                using var output = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var line = await input.ReadLineAsync(deadline.Token);
                if (line is null)
                    return;
                Reply reply;
                try
                {
                    var request = JsonSerializer.Deserialize(line, Wire.Default.Request)
                        ?? throw new JsonException("Expected a request object.");
                    reply = await dispatch(request, ct);
                }
                catch (JsonException ex) { reply = Reply.Fail("invalid_json", ex.Message); }
                await output.WriteLineAsync(JsonSerializer.Serialize(reply, Wire.Default.Reply).AsMemory(), ct);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }
}
