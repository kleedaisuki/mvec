using Mvec;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

if (args.Length != 2)
    throw new ArgumentException("Usage: Mvec.Journey <published mvec> <scratch-directory>");
await new Journey(Path.GetFullPath(args[0]), Path.GetFullPath(args[1])).RunAsync();

internal sealed class Journey(string executable, string scratch)
{
    private readonly ConcurrentDictionary<string, int> indexed = new();
    private readonly ConcurrentDictionary<string, Gate> gates = new();
    private int routerCalls;
    private int voyageCalls;
    private int largestBatch;
    private string endpoint = "";
    private const string Initial = "Native lowering emits machine code directly from the compiler intermediate representation.";
    private const string V2 = "V2GATE transient native lowering emits relocatable machine code from the intermediate representation.";
    private const string V3 = "V3GATE final native lowering translates compiler IR into register-allocated machine code.";
    private const string Offline = "OFFLINE Native lowering for the offline compiler target.";
    private const string Summary = "The module converts high-level compiler intermediate representation to optimized native code.";

    public async Task RunAsync()
    {
        Directory.CreateDirectory(scratch);
        var working = Path.Join(scratch, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(working);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        api.MapPost("/embeddings", EmbedAsync);
        using var chat = new OAuthFixture();
        chat.Map(api);
        await api.StartAsync();
        var address = api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        endpoint = address + "/embeddings";
        chat.ListenAt(address);
        try
        {
            await ExactAsync(Path.Join(working, "exact"));
            await ApproximateAsync(Path.Join(working, "ann"));
            await SummaryAsync(Path.Join(working, "oauth"), chat);
            Check(routerCalls > 0 && voyageCalls > 0 && largestBatch > 1, "both provider adapters and batching exercised");
            Console.WriteLine("mvec journey passed: exact and ANN, concurrent snapshots, deduplication, rename/delete, restart, isolation, summaries and MCP");
        }
        finally { Directory.Delete(working, recursive: true); }
    }

    private async Task ExactAsync(string project)
    {
        await InitAsync(project, "openrouter", "exact");
        Directory.CreateDirectory(Path.Join(project, "src"));
        Write(project, "src/lower.md", Initial);
        Write(project, "src/duplicate.md", Initial);
        Write(project, "docs.txt", new string('x', 127) + "\n" + Summary);
        Write(project, "secrets.env", "Not part of the semantic index");
        File.WriteAllBytes(Path.Join(project, "binary.dat"), [0, 1, 2]);
        JsonElement stable;
        await using (var daemon = StartDaemon(project))
        {
            stable = await WaitAsync(project, "initial exact index",
                s => Complete(s) && Count(s, "files") == 3 && Count(s, "chunks") == 4);
            Check(Count(stable, "vectors") == 3 && indexed[Initial] == 1, "cross-file content reused");

            var initial = await SearchAsync(project, Initial, 2);
            Check(Hits(initial).Length == 2 && Hits(initial).All(h => Text(h, "chunk") == Initial), "top-k keeps duplicate files");
            var located = Hits(await SearchAsync(project, Summary, 3)).Single(h => Text(h, "path") == "docs.txt");
            Check(Text(located, "chunk") == Summary && Count(located, "start_line") == 2
                && Count(located, "start_offset") == 128, "result offsets and lines refer to original content");

            await CallAsync(project, false, "serve");
            await using (var a = Hold("V2GATE"))
            await using (var b = Hold("V3GATE"))
            {
                Write(project, "src/lower.md", V2);
                await a.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(25));
                var readers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => SearchAsync(project, Initial, 3)));
                Check(readers.All(result => ChunkAt(result, "src/lower.md") == Initial),
                    "readers do not wait for a background HTTP request");
                Write(project, "src/lower.md", V3);
                a.Release();
                await b.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(25));
                Check(ChunkAt(await SearchAsync(project, Initial, 3), "src/lower.md") == Initial,
                    "superseded provider response never became the current file version");
                b.Release();
                await WaitAsync(project, "final version committed",
                    s => !s.GetProperty("busy").GetBoolean() && Count(s, "vectors") == 4);
                Check(ChunkAt(await SearchAsync(project, V3, 3), "src/lower.md") == V3, "final version is searchable");
            }

            File.Move(Path.Join(project, "src/duplicate.md"), Path.Join(project, "renamed.md"));
            Directory.Move(Path.Join(project, "src"), Path.Join(project, "moved"));
            await WaitPathsAsync(project, "rename and directory move", "renamed.md", "moved/lower.md", "docs.txt");
            Check(indexed[Initial] == 1 && indexed[V3] == 1, "moves do not re-embed");
            File.Delete(Path.Join(project, "renamed.md"));
            await WaitAsync(project, "delete", s => Count(s, "files") == 2 && Count(s, "vectors") == 3);
            Check(Count(await CallAsync(project, true, "search", "something", "--top-k", "100"), "hits") == 2,
                "top-k returns available distinct files when the index is smaller");

            var miss = await CallAsync(project, true, "summary", "docs.txt", "--cache-only");
            Check(Text(miss, "cache") == "miss", "cache-only summary works without a ChatGPT login");
            var denial = await RunAsync(project, "summary", "docs.txt");
            Check(denial.Exit == 1 && Text(Parse(denial.Out).GetProperty("error"), "code") == "chatgpt_sign_in_required",
                "the published daemon reports a missing OAuth session without spawning a program");
            await TestMcpAsync(project);
        }

        File.Delete(Path.Join(project, "docs.txt"));
        Write(project, "offline.md", Offline);
        await using var hold = Hold("OFFLINE");
        await using var reopened = StartDaemon(project);
        await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(25));
        var previous = await SearchAsync(project, V3, 2);
        Check(ChunkAt(previous, "moved/lower.md") == V3, "durable snapshot usable during restart reconciliation");
        hold.Release();
        await WaitPathsAsync(project, "restart reconciles offline changes", "moved/lower.md", "offline.md");
    }

    private async Task SummaryAsync(string projectPath, OAuthFixture fixture)
    {
        await InitAsync(projectPath, "openrouter", "exact");
        Write(projectPath, "summary.txt", Summary);
        var project = new Project(projectPath);
        using var http = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        var oauth = new ChatGptOAuth(project, http, fixture.Endpoints);
        await fixture.SignInAsync(oauth, ct);
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var daemon = Daemon.RunAsync(project, stop.Token, fixture.Endpoints);
            try
            {
                await WaitAsync(projectPath, "OAuth summary daemon", Complete);
                Check(fixture.Summaries == 0 && fixture.Refreshes == 0, "file indexing doesn't use the ChatGPT session");
                var fresh = await CallAsync(projectPath, true, "summary", "summary.txt");
                var cached = await CallAsync(projectPath, true, "summary", "summary.txt");
                Check(Text(fresh, "cache") == "generated" && Text(fresh, "summary") == "A concise OAuth summary."
                    && Text(cached, "cache") == "exact" && fixture.Summaries == 1 && fixture.Refreshes == 1,
                    "configured model uses a refreshed OAuth credential and only a completed stream is cached");
                Write(projectPath, "summary.txt", Summary + "!");
                var minor = await CallAsync(projectPath, true, "summary", "summary.txt");
                Check(Text(minor, "cache") == "small_change" && Text(minor, "summary_hash") != Text(minor, "document_hash")
                    && fixture.Summaries == 1, "small edit reuses a versioned summary");
                Write(projectPath, "summary.txt", "A new optimizer repairs invalid branches and simplifies control flow.");
                fixture.Interrupt = true;
                var interrupted = await RunAsync(projectPath, "summary", "summary.txt");
                Check(interrupted.Exit == 1 && Text(await CallAsync(projectPath, true, "summary", "summary.txt", "--cache-only"), "cache") == "miss",
                    "partial Responses streams never form cache entries");
                fixture.Interrupt = false;
                var major = await CallAsync(projectPath, true, "summary", "summary.txt");
                Check(Text(major, "cache") == "generated" && fixture.Summaries == 3, "large edit regenerates only on request");
                Check((await oauth.ModelsAsync(ct)).Single().Slug == OAuthFixture.Model, "account-specific list uses the same authorization");
                Check(await oauth.SignOutAsync(ct) && fixture.Revocations == 1, "logout revokes the rotated token");
            }
            finally { await stop.CancelAsync(); await daemon; }
        }
        await using var restarted = StartDaemon(projectPath);
        await WaitAsync(projectPath, "summary cache restart", Complete);
        var restart = await CallAsync(projectPath, true, "summary", "summary.txt");
        Check(Text(restart, "cache") == "exact" && fixture.Summaries == 3, "summary stays cached after restart and logout");
    }

    private async Task ApproximateAsync(string project)
    {
        await InitAsync(project, "voyage", "ann");
        for (var i = 0; i < 48; i++)
            Write(project, $"doc-{i:00}.md", $"Cluster-{i % 8} synthetic vector group; document {i:00}.");
        await using (var daemon = StartDaemon(project))
        {
            await WaitAsync(project, "ANN trained", s => Complete(s) && Text(s, "mode") == "ann" && Count(s, "files") == 48);
            var ann = await SearchAsync(project, "Cluster-4", 1);
            var exact = await SearchAsync(project, "Cluster-4", 1, "exact");
            Check(Text(ann, "mode") == "ann" && Text(exact, "mode") == "exact"
                && Text(Hits(ann).Single(), "chunk").StartsWith("Cluster-4", StringComparison.Ordinal)
                && Text(Hits(exact).Single(), "chunk").StartsWith("Cluster-4", StringComparison.Ordinal), "trained ANN and exact escape hatch");
            File.Delete(Path.Join(project, Text(Hits(ann).Single(), "path")));
            Write(project, "added.md", "Cluster-7 addition to the trained index.");
            await WaitAsync(project, "trained index updated in place",
                s => !s.GetProperty("busy").GetBoolean() && Count(s, "files") == 48 && indexed.ContainsKey("Cluster-7 addition to the trained index."));
            var updated = await SearchAsync(project, "Cluster-7 addition to the trained index.", 8);
            Check(Hits(updated).Any(h => Text(h, "path") == "added.md"), "ANN includes incremental insert");
        }
        await using var restart = StartDaemon(project);
        var persisted = await WaitAsync(project, "persisted ANN", s => Complete(s) && Count(s, "vectors") == 48);
        Check(Text(persisted, "mode") == "ann", "ANN model survives restart");
    }

    private async Task InitAsync(string project, string provider, string mode)
    {
        Directory.CreateDirectory(project);
        await RunAsync(project, "init");
        var name = provider == "voyage" ? "" : "mongodb/";
        File.WriteAllText(Path.Join(project, ".mvec", "config.toml"), $$"""
            [embedding]
            provider = "{{provider}}"
            route = "voyageai"
            model = "{{name}}voyage-4-large"
            query_model = "{{name}}voyage-4-lite"
            dimensions = 256
            batch_size = 8
            endpoint = "{{endpoint}}"
            key_env = "MVEC_TEST_KEY_NOT_SET"

            [search]
            mode = "{{mode}}"
            train_min_vectors = 16
            probe = 0.9

            [index]
            chunk_chars = 128

            [summary]
            enabled = true
            model = "{{OAuthFixture.Model}}"
            """);
    }

    private async Task EmbedAsync(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        var request = document.RootElement;
        var texts = request.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var type = Text(request, "input_type");
        var query = type is "query" or "search_query";
        var model = Text(request, "model");
        Check(model.EndsWith(query ? "voyage-4-lite" : "voyage-4-large", StringComparison.Ordinal), "correct model and input role");
        var direct = request.TryGetProperty("output_dimension", out var dimension);
        if (direct)
        {
            Interlocked.Increment(ref voyageCalls);
            Check(type is "query" or "document", "Voyage API input_type");
        }
        else
        {
            Interlocked.Increment(ref routerCalls);
            dimension = request.GetProperty("dimensions");
            Check(type is "search_query" or "search_document"
                && Text(request.GetProperty("provider").GetProperty("only")[0]) == "voyageai", "OpenRouter input_type and route");
        }

        if (!query)
        {
            Interlocked.Exchange(ref largestBatch, Math.Max(largestBatch, texts.Length));
            foreach (var text in texts)
            {
                indexed.AddOrUpdate(text, 1, (_, calls) => calls + 1);
                foreach (var (prefix, gate) in gates)
                {
                    if (!text.StartsWith(prefix, StringComparison.Ordinal))
                        continue;
                    gate.Arrived.TrySetResult();
                    await gate.Released.Task.WaitAsync(context.RequestAborted);
                }
            }
        }
        var data = texts.Select((text, index) => new { index, embedding = Vector(text, dimension.GetInt32()) }).Reverse();
        await context.Response.WriteAsJsonAsync(new { data });
    }

    private static float[] Vector(string text, int width)
    {
        var v = new float[width];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var cluster = text.IndexOf("Cluster-", StringComparison.Ordinal);
        if (cluster >= 0)
            v[text[cluster + 8] - '0'] = 16;
        else
            v[text.Contains("compiler", StringComparison.OrdinalIgnoreCase) ? 16 : 17] = 4;
        for (var n = 0; n < hash.Length; n++)
            v[32 + n] = (hash[n] - 128) / 128f;
        return v;
    }

    private Gate Hold(string prefix)
    {
        var gate = new Gate();
        Check(gates.TryAdd(prefix, gate), $"one gate per fixture prefix: {prefix}");
        return gate;
    }

    private async Task WaitPathsAsync(string project, string label, params string[] expected)
    {
        var until = Stopwatch.StartNew();
        do
        {
            var query = await SearchAsync(project, "native compiler lowering", 100);
            var paths = Hits(query).Select(h => Text(h, "path")).Order(StringComparer.Ordinal);
            if (paths.SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return;
            await Task.Delay(150);
        } while (until.Elapsed < TimeSpan.FromSeconds(25));
        throw new InvalidOperationException($"Timed out waiting for {label}.");
    }

    private async Task<JsonElement> WaitAsync(string project, string label, Func<JsonElement, bool> condition)
    {
        string? last = null;
        var until = Stopwatch.StartNew();
        do
        {
            var status = await RunAsync(project, "status");
            last = status.Out + status.Err;
            if (status.Exit == 0)
            {
                var result = Parse(status.Out).GetProperty("result");
                if (condition(result))
                    return result;
            }
            await Task.Delay(150);
        } while (until.Elapsed < TimeSpan.FromSeconds(35));
        throw new InvalidOperationException($"Timed out waiting for {label}: {last}");
    }

    private async Task TestMcpAsync(string project)
    {
        using var child = Start(project, "mcp");
        await child.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""");
        Check(Text(Parse((await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!)
            .GetProperty("result"), "protocolVersion") == "2025-11-25", "MCP handshake");
        await child.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await child.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"mvec_search","arguments":{"query":"native lowering","top_k":1}}}""");
        var result = Parse((await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!).GetProperty("result");
        Check(Hits(Parse(Text(result.GetProperty("content")[0], "text"))).Length == 1, "MCP forwards search to daemon");
        child.StandardInput.Close();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(child.ExitCode == 0, "MCP exits on stdin EOF");
    }

    private DaemonProcess StartDaemon(string project) => new(Start(project, "serve"));

    private async Task<JsonElement> SearchAsync(string project, string text, int k, string? mode = null)
    {
        string[] args = ["search", text, "--top-k", k.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        return await CallAsync(project, true, mode is null ? args : [..args, "--mode", mode]);
    }

    private async Task<JsonElement> CallAsync(string project, bool success, params string[] args)
    {
        var (exit, stdout, error) = await RunAsync(project, args);
        Check((exit == 0) == success, $"mvec {string.Join(' ', args)} exit {exit}: {stdout}{error}");
        return success ? Parse(stdout).GetProperty("result") : default;
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(string project, params string[] args)
    {
        using var child = Start(project, args);
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw new TimeoutException($"mvec {string.Join(' ', args)} did not exit.");
        }
        return (child.ExitCode, await output, await errors);
    }

    private Process Start(string project, params string[] args)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = project,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return Process.Start(psi)!;
    }

    private static void Write(string project, string path, string content) => File.WriteAllText(Path.Join(project, path), content);
    private static JsonElement Parse(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private static JsonElement[] Hits(JsonElement result) => result.GetProperty("hits").EnumerateArray().ToArray();
    private static string? ChunkAt(JsonElement result, string path) => Hits(result).Where(h => Text(h, "path") == path)
        .Select(h => Text(h, "chunk")).SingleOrDefault();
    private static long Count(JsonElement parent, string key)
    {
        var e = parent.GetProperty(key);
        return e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : e.GetInt64();
    }
    private static string Text(JsonElement parent, string? key = null) => (key is null ? parent : parent.GetProperty(key)).GetString()!;
    private static bool Complete(JsonElement status) => status.GetProperty("initial_scan_complete").GetBoolean()
        && !status.GetProperty("busy").GetBoolean();
    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Journey failed: {description}");
    }

    private sealed class Gate : IAsyncDisposable
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Released.TrySetResult();
        public ValueTask DisposeAsync() { Release(); return ValueTask.CompletedTask; }
    }

    private sealed class DaemonProcess(Process process) : IAsyncDisposable
    {
        private readonly Task<string> diagnostics = process.StandardError.ReadToEndAsync();
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var logs = await diagnostics;
            if (!string.IsNullOrWhiteSpace(logs))
                Console.Write(logs);
            process.Dispose();
        }
    }
}
