namespace Mvec;

internal static class Daemon
{
    public static async Task RunAsync(Project project, CancellationToken ct, ChatGptEndpoints? chatGptEndpoints = null,
        bool background = false)
    {
        using var owner = DaemonLock.TryAcquire(project);
        if (owner is null)
        {
            if (background)
                return;
            throw new IOException($"An mvec daemon already owns the index for {project.Root}.");
        }

        if (!background)
        {
            await RunServiceAsync(project, chatGptEndpoints, background, ct);
            return;
        }
        var stderr = Console.Error;
        using var log = new StreamWriter(new FileStream(project.DaemonLog, FileMode.Create, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
        Console.SetError(log);
        try { await RunServiceAsync(project, chatGptEndpoints, background, ct); }
        catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        finally { Console.SetError(stderr); }
    }

    private static async Task RunServiceAsync(Project project, ChatGptEndpoints? chatGptEndpoints, bool background,
        CancellationToken ct)
    {
        var configTime = background ? File.GetLastWriteTimeUtc(project.ConfigPath) : default;
        var config = Config.Load(project);
        using var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false
        }) { Timeout = TimeSpan.FromSeconds(90) };
        using var store = new Store(project, config);
        var embeddings = new Embeddings(http, config.Embedding);
        var endpoints = chatGptEndpoints ?? ChatGptEndpoints.Official;
        using var summaries = new Summaries(project, config, store,
            new ChatGptSummary(http, new ChatGptOAuth(project, http, endpoints), endpoints));
        using var indexer = new Indexer(project, config.Index, store, embeddings);
        var coldIndex = background && !store.HasSearchableChunks();
        var idle = background ? TimeSpan.FromSeconds(config.Daemon.IdleSeconds) : Timeout.InfiniteTimeSpan;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var reload = new CancellationTokenSource();
        var service = Ipc.ServeAsync(project, DispatchAsync, idle, stop.Token, reload.Token);
        var maintenance = Task.Run(() => indexer.RunAsync(stop.Token), CancellationToken.None);
        try
        {
            await await Task.WhenAny(service, maintenance);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
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
                if (background && (reload.IsCancellationRequested || File.GetLastWriteTimeUtc(project.ConfigPath) != configTime))
                {
                    await reload.CancelAsync();
                    return Reply.Fail(Ipc.Reloading, "The project configuration changed before this request ran.");
                }
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
            catch (ChatGptException ex) { return Reply.Fail(ex.Code, ex.Message); }
            catch (HttpRequestException ex) when (request.Method == "summary") { return Reply.Fail("chatgpt_request_failed", ex.Message); }
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
            if (coldIndex && await indexer.FirstIndexAsync(token) is { } failure)
                return Reply.Fail("indexing_failed", failure);
            var vector = (await embeddings.GetAsync([request.Query], query: true, token))[0];
            return Reply.From(store.Search(vector, k, request.Mode), Wire.Default.SearchReply);
        }
    }
}
