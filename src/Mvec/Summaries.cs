namespace Mvec;

internal sealed class Summaries(Project project, Config config, Store store, ChatGptSummary chatGpt) : IDisposable
{
    private readonly SemaphoreSlim generate = new(1);

    public async Task<SummaryReply> GetAsync(string path, bool cacheOnly, CancellationToken ct)
    {
        var relative = project.Relative(project.Resolve(path));
        if (!project.Includes(relative, config.Index))
            throw new ArgumentException($"The file is excluded by [index]: {relative}");

        await generate.WaitAsync(ct);
        try
        {
            var doc = Document.Read(project, relative, config.Index.MaxFileBytes)
                ?? throw new FileNotFoundException($"No supported text file at {relative}.");
            var settings = config.Summary;
            var saved = store.FindSummary(settings.Profile, doc.Hash, relative);
            if (saved?.Hash == doc.Hash)
            {
                store.LinkSummary(settings.Profile, relative, saved.Hash);
                return new(relative, doc.Hash, saved.Hash, saved.Text, "exact");
            }
            if (saved is not null && ChangeFraction(saved.Source, doc.Text) < settings.ChangeThreshold)
                return new(relative, doc.Hash, saved.Hash, saved.Text, "small_change");
            if (cacheOnly)
                return new(relative, doc.Hash, null, null, "miss");
            if (!settings.Enabled)
                throw new InvalidOperationException("Enable [summary] before generating ChatGPT summaries; then run mvec login.");
            if (doc.Text.Length > settings.MaxChars)
                throw new InvalidDataException($"Document exceeds [summary].max_chars: {relative}");

            var text = await chatGpt.GenerateAsync(settings.Model, doc.Text, ct);
            store.SaveSummary(settings.Profile, relative, new(doc.Hash, doc.Text, text));
            var current = Document.Read(project, relative, config.Index.MaxFileBytes);
            return new(relative, current?.Hash, doc.Hash, text, current?.Hash == doc.Hash ? "generated" : "superseded");
        }
        finally { generate.Release(); }
    }

    // Measure changed characters between two baselines, discounting unchanged complete lines
    // (including moves). Keep the original baseline across small edits so they accumulate.
    private static double ChangeFraction(string before, string after)
    {
        var length = Math.Max(before.Length, after.Length);
        if (length == 0)
            return 0;
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix
            && before[^(suffix + 1)] == after[^(suffix + 1)])
            suffix++;

        var left = before[prefix..(before.Length - suffix)];
        var right = after[prefix..(after.Length - suffix)];
        var unchanged = 0;
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in left.Split('\n'))
            lines[line] = lines.GetValueOrDefault(line) + 1;
        foreach (var line in right.Split('\n'))
        {
            if (lines.TryGetValue(line, out var count) && count > 0)
            {
                unchanged += line.Length;
                lines[line] = count - 1;
            }
        }
        return (double)(Math.Max(left.Length, right.Length) - unchanged) / length;
    }

    public void Dispose() => generate.Dispose();
}
