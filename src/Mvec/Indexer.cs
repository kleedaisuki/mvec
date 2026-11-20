using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Mvec;

internal sealed class Indexer : IDisposable
{
    private const int GroupSize = 16;
    private readonly Project project;
    private readonly IndexConfig config;
    private readonly Store store;
    private readonly Embeddings embeddings;
    private readonly FileSystemWatcher watcher;
    private readonly string recipe;
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.Ordinal);
    private readonly Channel<byte> wake = Channel.CreateBounded<byte>(1);
    private int needsScan = 1;
    private volatile bool busy = true;
    private volatile bool initialScanComplete;
    private volatile string? error;

    public Indexer(Project project, IndexConfig config, Store store, Embeddings embeddings)
    {
        this.project = project;
        this.config = config;
        this.store = store;
        this.embeddings = embeddings;
        recipe = FormattableString.Invariant($"line-v1/{config.ChunkChars}");
        watcher = new(project.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Created += (_, e) => Signal(e.FullPath);
        watcher.Changed += (_, e) => Signal(e.FullPath);
        watcher.Deleted += (_, e) => Signal(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            if (OperatingSystem.IsWindows() && Project.PathComparer.Equals(e.FullPath, e.OldFullPath))
                Scan();
            else
            {
                Signal(e.FullPath);
                Signal(e.OldFullPath);
            }
        };
        watcher.Error += (_, e) =>
        {
            Report("watcher", e.GetException());
            Scan();
        };
        watcher.EnableRaisingEvents = true;
        wake.Writer.TryWrite(0);
    }

    public DaemonStatus Status()
    {
        var s = store.Statistics();
        return new(s.Files, s.Chunks, s.Vectors, s.Mode, s.Vec1, initialScanComplete, busy, pending.Count, error);
    }

    private void Signal(string fullPath) => Queue(project.Relative(fullPath));

    private void Queue(string path)
    {
        if (path == "." || !project.Includes(path, config))
            return;
        pending.TryAdd(path, 0);
        wake.Writer.TryWrite(0);
    }

    private void Scan()
    {
        Interlocked.Exchange(ref needsScan, 1);
        wake.Writer.TryWrite(0);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.ReconcileSeconds));
        var recovery = RecoverAsync(timer, ct);
        var retrySeconds = 1;
        try
        {
            while (await wake.Reader.WaitToReadAsync(ct))
            {
                busy = true;
                wake.Reader.TryRead(out _);
                error = null;
                try
                {
                    if (Interlocked.Exchange(ref needsScan, 0) != 0)
                    {
                        await ReconcileAsync(ct);
                        initialScanComplete = true;
                    }
                    await ProcessEventsAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    store.TrainIfReady();
                    retrySeconds = 1;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Report("indexing", ex);
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), ct);
                    retrySeconds = Math.Min(retrySeconds * 2, 60);
                    Scan();
                }
                finally { busy = false; }
            }
        }
        finally
        {
            timer.Dispose();
            try { await recovery; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private async Task RecoverAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct))
            Scan();
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var known = store.Paths().ToHashSet(StringComparer.Ordinal);
        var batch = new List<PendingFile>(GroupSize);
        var observed = OperatingSystem.IsWindows() ? new HashSet<string>(Project.PathComparer) : null;
        foreach (var path in Files(project.Root))
        {
            known.Remove(path);
            observed?.Add(path);
            await StageAsync(path, batch, ct);
        }
        foreach (var path in known)
            await StageAsync(path, batch, ct, missing: observed?.Contains(path) == true || !project.Includes(path, config));
        await ApplyAsync(batch, ct);
    }

    private IEnumerable<string> Files(string directory)
    {
        var dirs = new Stack<string>();
        dirs.Push(directory);
        while (dirs.TryPop(out var dir))
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch (DirectoryNotFoundException) { continue; }
            catch (UnauthorizedAccessException ex) { Report(project.Relative(dir), ex); continue; }
            foreach (var entry in entries)
            {
                var path = project.Relative(entry);
                if (!project.Includes(path, config))
                    continue;
                var attrs = Attributes(entry);
                if (attrs is null || (attrs & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attrs & FileAttributes.Directory) != 0)
                    dirs.Push(entry);
                else
                    yield return path;
            }
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        var batch = new List<PendingFile>(GroupSize);
        foreach (var path in pending.Keys)
        {
            pending.TryRemove(path, out _);
            var absolute = project.Resolve(path);
            var attrs = Attributes(absolute);
            if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == FileAttributes.Directory
                && !project.IsLink(path))
            {
                await StageAsync(path, batch, ct);
                foreach (var descendant in Files(absolute))
                    await StageAsync(descendant, batch, ct);
                continue;
            }

            await StageAsync(path, batch, ct);
            if (attrs is null)
                foreach (var descendant in store.Descendants(path))
                    await StageAsync(descendant, batch, ct);
        }
        await ApplyAsync(batch, ct);
    }

    private async Task StageAsync(string path, List<PendingFile> batch, CancellationToken ct, bool missing = false)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var doc = missing ? null : Document.Read(project, path, config.MaxFileBytes);
            if (doc is not null && store.IsIndexed(doc, recipe))
                return;
            if (doc is null && !store.HasPath(path))
                return;
            batch.Add(new(path, doc, doc?.Chunks(config.ChunkChars).ToArray() ?? [], missing));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(path, ex);
        }
        if (batch.Count == GroupSize)
            await ApplyAsync(batch, ct);
    }

    private async Task ApplyAsync(List<PendingFile> batch, CancellationToken ct)
    {
        foreach (var file in batch)
            if (file.Document is null)
                Commit(file, ct);
        var missing = batch.SelectMany(x => x.Chunks).DistinctBy(x => x.Hash).Where(x => !store.HasEmbedding(x.Hash));
        foreach (var chunks in missing.Chunk(embeddings.BatchSize))
        {
            var vectors = await embeddings.GetAsync(chunks.Select(x => x.Text).ToArray(), query: false, ct);
            store.SaveEmbeddings(chunks, vectors);
        }
        foreach (var file in batch)
            if (file.Document is not null)
                Commit(file, ct);
        batch.Clear();
    }

    private void Commit(PendingFile file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // An embedding response belongs to its input snapshot, not necessarily the file now at this path.
            var current = file.Excluded ? null : Document.Read(project, file.Path, config.MaxFileBytes);
            if (current?.Hash != file.Document?.Hash)
                Queue(file.Path);
            else
                store.Replace(file.Path, recipe, file.Document, file.Chunks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(file.Path, ex);
        }
    }

    private void Report(string context, Exception ex)
    {
        error = $"{context}: {ex.Message}";
        Console.Error.WriteLine($"mvec: {error}");
    }

    public void Dispose() => watcher.Dispose();

    private sealed record PendingFile(string Path, Document? Document, Chunk[] Chunks, bool Excluded);
}
