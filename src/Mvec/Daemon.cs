namespace Mvec;

internal static class Daemon
{
    public static async Task RunAsync(Project project, CancellationToken ct)
    {
        var config = Config.Load(project);
        using var owner = Acquire(project);
        using var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false
        }) { Timeout = TimeSpan.FromSeconds(90) };
        using var store = new Store(project, config);
        var embeddings = new Embeddings(http, config.Embedding);
        using var summaries = new Summaries(project, config, store);
        using var indexer = new Indexer(project, config.Index, store, embeddings);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var service = Ipc.ServeAsync(project, DispatchAsync, stop.Token);
        var maintenance = Task.Run(() => indexer.RunAsync(stop.Token), CancellationToken.None);
        try
        {
            await await Task.WhenAny(service, maintenance);
        }
        finally
        {
            await stop.CancelAsync();
            try { await Task.WhenAll(service, maintenance); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        async Task<Reply> DispatchAsync(Request request, CancellationToken token)
        {
            try
            {
                return request.Method switch
                {
                    "status" => Reply.From(indexer.Status(), Wire.Default.DaemonStatus),
                    "search" => await SearchAsync(request, token),
                    "summary" when !string.IsNullOrWhiteSpace(request.Path) => Reply.From(
                        await summaries.GetAsync(request.Path, request.CacheOnly, token), Wire.Default.SummaryReply),
                    "summary" => Reply.Fail("invalid_request", "summary requires a path."),
                    _ => Reply.Fail("unknown_method", $"Unknown method: {request.Method}")
                };
            }
            catch (ArgumentException ex) { return Reply.Fail("invalid_request", ex.Message); }
            catch (FileNotFoundException ex) { return Reply.Fail("not_found", ex.Message); }
            catch (HttpRequestException ex) { return Reply.Fail("embedding_failed", ex.Message); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return Reply.Fail("timeout", $"{request.Method} provider timed out.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"mvec: {request.Method}: {ex}");
                return Reply.Fail("operation_failed", ex.Message);
            }
        }

        async Task<Reply> SearchAsync(Request request, CancellationToken token)
        {
            var k = request.TopK ?? 10;
            if (k is < 1 or > 100 || request.Mode is not (null or "exact" or "ann")
                || string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 16000)
                return Reply.Fail("invalid_request", "search requires a query of 1–16000 characters, top_k of 1–100 and mode exact or ann.");
            var vector = (await embeddings.GetAsync([request.Query], query: true, token))[0];
            return Reply.From(store.Search(vector, k, request.Mode), Wire.Default.SearchReply);
        }
    }

    private static FileStream Acquire(Project project)
    {
        try { return new FileStream(Path.Join(project.State, "daemon.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException($"Cannot acquire the daemon lock for {project.Root}: {ex.Message}", ex); }
    }
}
