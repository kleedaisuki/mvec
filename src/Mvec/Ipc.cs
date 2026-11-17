using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Mvec;

internal static class Ipc
{
    public static async Task<Reply> CallAsync(Project project, Request request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var stream = await ConnectAsync(project, timeout.Token);
        using var output = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var input = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await output.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Default.Request).AsMemory(), ct);
        var line = await input.ReadLineAsync(ct) ?? throw new IOException("Daemon disconnected without a reply.");
        return JsonSerializer.Deserialize(line, Wire.Default.Reply) ?? throw new IOException("Daemon sent an empty reply.");
    }

    private static async Task<Stream> ConnectAsync(Project project, CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", project.Pipe, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.ConnectAsync(ct); return pipe; }
                catch { pipe.Dispose(); throw; }
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(project.Socket), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            throw new IOException($"Cannot connect to the mvec daemon for {project.Root}. Run mvec serve.", ex);
        }
    }

    public static async Task ServeAsync(Project project, Func<Request, CancellationToken, Task<Reply>> dispatch, CancellationToken ct)
    {
        var clients = new ConcurrentDictionary<Task, byte>();
        try
        {
            if (OperatingSystem.IsWindows())
                await AcceptPipesAsync();
            else
                await AcceptSocketsAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients.Keys); }

        async Task AcceptPipesAsync()
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(project.Pipe, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(ct); }
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
                while (!ct.IsCancellationRequested)
                    Add(new NetworkStream(await socket.AcceptAsync(ct), ownsSocket: true));
            }
            finally { File.Delete(project.Socket); }
        }

        void Add(Stream stream)
        {
            var handler = Task.Run(() => HandleAsync(stream, dispatch, ct), CancellationToken.None);
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
