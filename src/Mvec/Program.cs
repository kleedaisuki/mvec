using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Mvec;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            ctx => { ctx.Cancel = true; shutdown.Cancel(); });

        try
        {
            return await RunAsync(args, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"mvec: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine("""
                Usage: mvec <command> [--project DIR] [options]
                  init                 Write .mvec/config.toml
                  serve                Run the project's daemon in the foreground
                  status               Show daemon and index status
                  search QUERY         Search files [--top-k N] [--mode exact|ann]
                  summary PATH         Get/generate a document summary [--cache-only]
                  login                Authorize ChatGPT plan usage [--no-browser] [--port N]
                  logout               Disconnect this project's ChatGPT session
                  models               List ChatGPT model slugs for [summary].model
                  mcp                  Serve MCP over stdio, forwarding to the daemon
                Without --project, use the nearest configured parent project.
                """);
            return 0;
        }

        var command = args[0];
        var positional = new List<string>();
        string? root = null;
        string? mode = null;
        int? k = null;
        var cacheOnly = false;
        var noBrowser = false;
        var port = 0;
        var parseOptions = true;
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (parseOptions && arg == "--") { parseOptions = false; continue; }
            if (!parseOptions || !arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }
            switch (arg)
            {
                case "--project": root = Value(); break;
                case "--top-k" when command == "search": k = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--mode" when command == "search": mode = Value(); break;
                case "--cache-only" when command == "summary": cacheOnly = true; break;
                case "--no-browser" when command == "login": noBrowser = true; break;
                case "--port" when command == "login": port = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Unknown option for {command}: {arg}");
            }
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"Expected a value for {arg}.");
        }

        var project = new Project(root ?? (command == "init" ? Environment.CurrentDirectory : Discover()));
        if (positional.Count != (command is "search" or "summary" ? 1 : 0))
            throw new ArgumentException($"Wrong number of arguments for {command}. Run mvec --help.");

        switch (command)
        {
            case "init":
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(project.State);
                else
                    Directory.CreateDirectory(project.State, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                using (var stream = new FileStream(project.ConfigPath, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.WriteLine(Config.Template);
                Console.WriteLine(project.ConfigPath);
                return 0;
            case "serve":
                await Daemon.RunAsync(project, ct);
                return 0;
            case "login":
            case "logout":
            case "models":
                return await RunAccountAsync(project, command, noBrowser, port, ct);
            case "mcp":
                await Mcp.RunAsync(project, ct);
                return 0;
            case "status":
            case "search":
            case "summary":
                var request = new Request(command, command == "search" ? positional[0] : null, k, mode,
                    command == "summary" ? positional[0] : null, cacheOnly);
                var response = await Ipc.CallAsync(project, request, ct);
                Console.WriteLine(JsonSerializer.Serialize(response, Wire.Default.Reply));
                return response.Ok ? 0 : 1;
            default:
                throw new ArgumentException($"Unknown command: {command}");
        }
    }

    private static async Task<int> RunAccountAsync(Project project, string command, bool noBrowser, int port, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(30) };
        var oauth = new ChatGptOAuth(project, http, ChatGptEndpoints.Official);
        switch (command)
        {
            case "login":
                if (!File.Exists(project.ConfigPath))
                    throw new InvalidOperationException("Run mvec init in this project first.");
                var account = await oauth.SignInAsync(port, uri =>
                {
                    Console.Error.WriteLine($"Continue with ChatGPT: {uri.AbsoluteUri}");
                    if (!noBrowser)
                    {
                        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
                        catch (Win32Exception) { Console.Error.WriteLine("Open the URL in a browser to continue."); }
                    }
                }, ct);
                Console.WriteLine($"Authorized ChatGPT plan usage for {account}.");
                return 0;
            case "logout":
                var revoked = await oauth.SignOutAsync(ct);
                Console.WriteLine(revoked ? "Signed out of ChatGPT for this project."
                    : "Local ChatGPT session removed. Remote revocation was not confirmed; disconnect mvec in ChatGPT Settings.");
                return revoked ? 0 : 1;
            default:
                foreach (var model in await oauth.ModelsAsync(ct))
                    Console.WriteLine($"{model.Slug}\t{model.Name}");
                return 0;
        }
    }

    private static string Discover()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Join(dir.FullName, ".mvec", "config.toml")))
                return dir.FullName;
        throw new InvalidOperationException("No configured mvec project in this directory or its parents. Run mvec init.");
    }
}
