using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mvec;

internal static partial class DaemonClient
{
    public static async Task<Stream> ConnectAsync(Project project, CancellationToken ct)
    {
        if (await Ipc.TryConnectAsync(project, ct) is { } running)
            return running;
        if (!File.Exists(project.ConfigPath))
            throw new IOException("No mvec project configuration. Run mvec init in the project first.");

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(TimeSpan.FromSeconds(12));
        FileStream? launch = null;
        Process? child = null;
        try
        {
            while (true)
            {
                var token = startup.Token;
                if (await Ipc.TryConnectAsync(project, token) is { } connected)
                    return connected;
                if (child is { HasExited: true, ExitCode: not 0 })
                {
                    var error = (await child.StandardError.ReadToEndAsync(token)).Trim();
                    throw new IOException(error.Length == 0 ? $"mvec daemon exited with code {child.ExitCode}." : error);
                }
                if (child is { HasExited: true })
                {
                    child.Dispose();
                    child = null;
                }

                // Only one client launches at a time. The daemon itself still owns the
                // index lock, including when started explicitly in the foreground.
                launch ??= DaemonLock.TryAcquire(project, "launch.lock");
                if (launch is not null && child is null && CanLaunch(project))
                    child = Start(project);

                await Task.Delay(40, token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"Timed out connecting to the mvec daemon for {project.Root}. See {project.DaemonLog}.");
        }
        finally
        {
            child?.Dispose();
            launch?.Dispose();
        }
    }

    private static bool CanLaunch(Project project)
    {
        using var owner = DaemonLock.TryAcquire(project);
        return owner is not null;
    }

    private static Process Start(Project project)
    {
        if (OperatingSystem.IsWindows())
            IsolateWindowsStdio();
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            WorkingDirectory = project.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(info.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.Join(AppContext.BaseDirectory, "mvec.dll"));
        info.ArgumentList.Add("__daemon");
        info.ArgumentList.Add(project.Root);
        try { return Process.Start(info) ?? throw new IOException("Could not launch the mvec daemon."); }
        catch (Win32Exception ex) { throw new IOException($"Could not start the mvec daemon: {ex.Message}", ex); }
    }

    private static void IsolateWindowsStdio()
    {
        const uint inherit = 1;
        const int standardInput = -10, standardError = -12;
        // .NET 10 CreateProcess inherits existing handles even with all three
        // streams redirected. Keeping the caller's pipes open delays their EOF.
        for (var stream = standardInput; stream >= standardError; stream--)
        {
            var handle = GetStdHandle(stream);
            if (GetHandleInformation(handle, out var flags) == 0 || (flags & inherit) == 0
                || SetHandleInformation(handle, inherit, 0) != 0)
                continue;
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            throw new IOException($"Could not isolate daemon streams: {error.Message}", error);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int stream);

    [LibraryImport("kernel32.dll")]
    private static partial int GetHandleInformation(nint handle, out uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetHandleInformation(nint handle, uint mask, uint flags);
}

internal static class DaemonLock
{
    public static FileStream? TryAcquire(Project project, string name = "daemon.lock")
    {
        try { return new FileStream(Path.Join(project.State, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        // FileStream reports the Win32 sharing violation or the native EWOULDBLOCK.
        catch (IOException ex) when (ex.HResult == (OperatingSystem.IsWindows() ? unchecked((int)0x80070020)
            : OperatingSystem.IsMacOS() ? 35 : 11))
        {
            return null;
        }
    }
}
