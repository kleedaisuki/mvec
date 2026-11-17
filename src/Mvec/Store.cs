using Microsoft.Data.Sqlite;

namespace Mvec;

internal sealed class Store : IDisposable
{
    private const int MaxBuckets = 128;
    private const string Flat = """{"index":"flat","distance":"cos"}""";
    private readonly string connection;
    private readonly SqliteConnection writer;
    private readonly object readGate = new();
    private readonly SqliteConnection reader;
    private readonly SearchConfig search;

    public Store(Project project, Config config)
    {
        connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Join(project.State, "index.db"),
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        search = config.Search;
        writer = Open();
        try
        {
            using var wal = Sql(writer, "PRAGMA journal_mode=WAL");
            if (!Equals(wal.ExecuteScalar(), "wal"))
                throw new InvalidOperationException("The index database requires SQLite WAL mode.");
            Initialize(config);
            reader = Open();
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private SqliteConnection Open(bool vectors = true)
    {
        var db = new SqliteConnection(connection);
        try
        {
            db.Open();
            if (vectors)
            {
                var name = OperatingSystem.IsWindows() ? "vec1.dll" : OperatingSystem.IsMacOS() ? "vec1.dylib" : "vec1.so";
                db.LoadExtension(Path.Join(AppContext.BaseDirectory, name));
            }
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private void Initialize(Config config)
    {
        using var tx = writer.BeginTransaction();
        using var schema = Sql(writer, """
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS files(
                path TEXT PRIMARY KEY, hash TEXT NOT NULL, recipe TEXT NOT NULL
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS embeddings(
                id INTEGER PRIMARY KEY, hash TEXT NOT NULL UNIQUE, text TEXT NOT NULL, vector BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS chunks(
                path TEXT NOT NULL REFERENCES files(path) ON DELETE CASCADE,
                embedding INTEGER NOT NULL REFERENCES embeddings(id),
                start_offset INTEGER NOT NULL, end_offset INTEGER NOT NULL,
                start_line INTEGER NOT NULL, end_line INTEGER NOT NULL,
                PRIMARY KEY(path, start_offset)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS chunks_embedding ON chunks(embedding);
            CREATE VIRTUAL TABLE IF NOT EXISTS vectors USING vec1(vector);

            CREATE TABLE IF NOT EXISTS summaries(
                profile TEXT NOT NULL, hash TEXT NOT NULL, source TEXT NOT NULL, summary TEXT NOT NULL,
                PRIMARY KEY(profile, hash)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS summary_paths(
                profile TEXT NOT NULL, path TEXT NOT NULL, hash TEXT NOT NULL,
                PRIMARY KEY(profile, path),
                FOREIGN KEY(profile, hash) REFERENCES summaries(profile, hash)
            ) WITHOUT ROWID;
            """, tx);
        schema.ExecuteNonQuery();
        var space = Meta(writer, "space", tx);
        if (space != config.Embedding.Space)
        {
            using var clear = Sql(writer, "DELETE FROM files; DELETE FROM vectors; DELETE FROM embeddings;", tx);
            clear.ExecuteNonQuery();
            RebuildFlat(tx);
            SetMeta("space", config.Embedding.Space, tx);
        }
        else if (search.Mode == "exact" && Meta(writer, "mode", tx) == "ann")
        {
            RebuildFlat(tx);
        }
        tx.Commit();
    }

    private void RebuildFlat(SqliteTransaction tx)
    {
        using var rebuild = Sql(writer, "INSERT INTO vectors(cmd,arg) VALUES('rebuild',$flat)", tx, ("$flat", Flat));
        rebuild.ExecuteNonQuery();
        SetMeta("mode", "exact", tx);
    }

    public bool IsIndexed(Document doc, string recipe)
    {
        using var cmd = Sql(writer, "SELECT 1 FROM files WHERE path=$path AND hash=$hash AND recipe=$recipe",
            null, ("$path", doc.Path), ("$hash", doc.Hash), ("$recipe", recipe));
        return cmd.ExecuteScalar() is not null;
    }

    public bool HasPath(string path)
    {
        using var cmd = Sql(writer, "SELECT 1 FROM files WHERE path=$path", null, ("$path", path));
        return cmd.ExecuteScalar() is not null;
    }

    public string[] Descendants(string path)
    {
        // The binary range contains exactly paths with this directory prefix.
        using var cmd = Sql(writer, "SELECT path FROM files WHERE path >= $start AND path < $end", null,
            ("$start", path + "/"), ("$end", path + "0"));
        return ReadPaths(cmd);
    }

    public string[] Paths()
    {
        using var cmd = Sql(writer, "SELECT path FROM files");
        return ReadPaths(cmd);
    }

    private static string[] ReadPaths(SqliteCommand cmd)
    {
        using var rows = cmd.ExecuteReader();
        var paths = new List<string>();
        while (rows.Read())
            paths.Add(rows.GetString(0));
        return paths.ToArray();
    }

    public bool HasEmbedding(string hash)
    {
        using var cmd = Sql(writer, "SELECT id FROM embeddings WHERE hash=$hash", null, ("$hash", hash));
        return cmd.ExecuteScalar() is not null;
    }

    public void SaveEmbeddings(IReadOnlyList<Chunk> chunks, byte[][] vectors)
    {
        using var tx = writer.BeginTransaction();
        using var cmd = Sql(writer, "INSERT INTO embeddings(hash,text,vector) VALUES($hash,$text,$vector)", tx,
            ("$hash", ""), ("$text", ""), ("$vector", Array.Empty<byte>()));
        for (var i = 0; i < chunks.Count; i++)
        {
            cmd.Parameters[0].Value = chunks[i].Hash;
            cmd.Parameters[1].Value = chunks[i].Text;
            cmd.Parameters[2].Value = vectors[i];
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Replace(string path, string recipe, Document? document, Chunk[] chunks)
    {
        using var tx = writer.BeginTransaction();
        var previous = OldEmbeddings(path, tx);
        if (document is null)
        {
            using var remove = Sql(writer, "DELETE FROM files WHERE path=$path", tx, ("$path", path));
            remove.ExecuteNonQuery();
        }
        else
        {
            using var upsert = Sql(writer, """
                INSERT INTO files(path,hash,recipe) VALUES($path,$hash,$recipe)
                ON CONFLICT(path) DO UPDATE SET hash=excluded.hash, recipe=excluded.recipe;
                DELETE FROM chunks WHERE path=$path;
                """, tx, ("$path", path), ("$hash", document.Hash), ("$recipe", recipe));
            upsert.ExecuteNonQuery();
            AddChunks(path, chunks, tx);
        }
        using var prune = Sql(writer, """
            DELETE FROM vectors WHERE rowid=$id AND NOT EXISTS (SELECT 1 FROM chunks WHERE embedding=$id)
            """, tx, ("$id", 0L));
        foreach (var id in previous)
        {
            prune.Parameters[0].Value = id;
            prune.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private List<long> OldEmbeddings(string path, SqliteTransaction tx)
    {
        using var cmd = Sql(writer, "SELECT DISTINCT embedding FROM chunks WHERE path=$path", tx, ("$path", path));
        using var rows = cmd.ExecuteReader();
        var ids = new List<long>();
        while (rows.Read())
            ids.Add(rows.GetInt64(0));
        return ids;
    }

    private void AddChunks(string path, Chunk[] chunks, SqliteTransaction tx)
    {
        using var add = Sql(writer, """
            INSERT INTO chunks
            SELECT $path, id, $start, $end, $first, $last FROM embeddings WHERE hash=$hash
            RETURNING embedding;
            """, tx, ("$path", path), ("$start", 0), ("$end", 0), ("$first", 0), ("$last", 0), ("$hash", ""));
        using var activate = Sql(writer, """
            INSERT INTO vectors(rowid,vector)
            SELECT id,vector FROM embeddings
            WHERE id=$id AND NOT EXISTS (SELECT 1 FROM vectors WHERE rowid=$id)
            """, tx, ("$id", 0L));
        foreach (var chunk in chunks)
        {
            add.Parameters[1].Value = chunk.StartOffset;
            add.Parameters[2].Value = chunk.EndOffset;
            add.Parameters[3].Value = chunk.StartLine;
            add.Parameters[4].Value = chunk.EndLine;
            add.Parameters[5].Value = chunk.Hash;
            activate.Parameters[0].Value = add.ExecuteScalar()
                ?? throw new InvalidOperationException($"Missing cached embedding for {path}:{chunk.StartLine}.");
            activate.ExecuteNonQuery();
        }
    }

    public void TrainIfReady()
    {
        if (search.Mode != "ann" || Meta(writer, "mode") == "ann")
            return;
        using var count = Sql(writer, "SELECT count(*) FROM vectors");
        var size = (long)count.ExecuteScalar()!;
        if (size < search.TrainMinVectors)
            return;

        var buckets = Math.Clamp((int)Math.Sqrt(size), 2, MaxBuckets);
        var sample = (int)Math.Min(size, 100L * buckets);
        var options = FormattableString.Invariant($$"""{"nbucket":{{buckets}},"distance":"cos","quantizer":"none"}""");
        using var tx = writer.BeginTransaction();
        using var train = Sql(writer, """
            INSERT INTO vectors(cmd,arg)
            SELECT 'rebuild', vec1_train(vector,$options)
            FROM (SELECT vector FROM vectors ORDER BY random() LIMIT $sample)
            """, tx, ("$options", options), ("$sample", sample));
        train.ExecuteNonQuery();
        SetMeta("mode", "ann", tx);
        tx.Commit();
        Console.Error.WriteLine($"mvec: trained Vec1 ANN ({size} live vectors; {buckets} buckets, {sample} training vectors)");
    }

    public SearchReply Search(byte[] vector, int k, string? requestedMode)
    {
        lock (readGate)
        {
            using var tx = reader.BeginTransaction(deferred: true);
            var trained = Meta(reader, "mode", tx) == "ann";
            var ann = trained && requestedMode != "exact";
            var mode = ann ? "ann" : "exact";
            var budget = ann ? k * search.Candidates : k;
            // Vec1 interprets 1.0 as one absolute bucket, not 100%.
            var probe = search.Probe == 1 ? MaxBuckets : search.Probe;
            while (true)
            {
                var opts = FormattableString.Invariant($$"""{"K":{{budget}},"nprobe":{{probe}}}""");
                var candidates = trained && !ann
                    ? "SELECT rowid, vec1_cos_distance($vector,vector) AS d FROM vectors ORDER BY d,rowid LIMIT $budget"
                    : "SELECT rowid, distance AS d FROM vectors($vector,$opts)";
                using var cmd = Sql(reader, $$"""
                    WITH nearest AS MATERIALIZED ({{candidates}}),
                    ranked AS (
                        SELECT c.path,e.text,c.start_offset,c.end_offset,c.start_line,c.end_line,n.d,
                            row_number() OVER (PARTITION BY c.path ORDER BY n.d,c.start_offset) AS rank
                        FROM nearest n JOIN chunks c ON c.embedding=n.rowid
                        JOIN embeddings e ON e.id=n.rowid
                    )
                    SELECT path,text,start_offset,end_offset,start_line,end_line,d
                    FROM ranked WHERE rank=1 ORDER BY d,path LIMIT $k
                    """, tx, ("$vector", vector), ("$opts", opts), ("$budget", budget), ("$k", k));
                using var rows = cmd.ExecuteReader();
                var hits = new List<SearchHit>();
                while (rows.Read())
                    hits.Add(new(rows.GetString(0), Math.Clamp(1 - rows.GetDouble(6), -1, 1),
                        rows.GetString(1), rows.GetInt32(2), rows.GetInt32(3), rows.GetInt32(4), rows.GetInt32(5)));

                if (hits.Count == k || ann || budget == 33_554_432)
                    return new(mode, hits.ToArray());
                rows.Close();
                using var count = Sql(reader, "SELECT count(*) FROM vectors", tx);
                var available = (long)count.ExecuteScalar()!;
                if (available <= budget)
                    return new(mode, hits.ToArray());
                budget = (int)Math.Min(Math.Min(available, budget * 2L), 33_554_432);
            }
        }
    }

    public (long Files, long Chunks, long Vectors, string Mode, string Vec1) Statistics()
    {
        lock (readGate)
        {
            using var cmd = Sql(reader, """
                SELECT (SELECT count(*) FROM files), (SELECT count(*) FROM chunks),
                    (SELECT count(*) FROM vectors), (SELECT value FROM metadata WHERE key='mode'), vec1_info()
                """);
            using var rows = cmd.ExecuteReader();
            rows.Read();
            return (rows.GetInt64(0), rows.GetInt64(1), rows.GetInt64(2), rows.GetString(3), rows.GetString(4));
        }
    }

    public SavedSummary? FindSummary(string profile, string hash, string path)
    {
        lock (readGate)
        {
            using var cmd = Sql(reader, """
                SELECT hash,source,summary FROM summaries
                WHERE profile=$profile AND hash IN ($hash, (SELECT hash FROM summary_paths WHERE profile=$profile AND path=$path))
                ORDER BY hash=$hash DESC LIMIT 1
                """, null, ("$profile", profile), ("$hash", hash), ("$path", path));
            using var rows = cmd.ExecuteReader();
            return rows.Read() ? new(rows.GetString(0), rows.GetString(1), rows.GetString(2)) : null;
        }
    }

    public void LinkSummary(string profile, string path, string hash)
    {
        using (var existing = Open(vectors: false))
        using (var cmd = Sql(existing, """
            INSERT INTO summary_paths(profile,path,hash) VALUES($profile,$path,$hash)
            ON CONFLICT(profile,path) DO UPDATE SET hash=excluded.hash WHERE hash<>excluded.hash
            """, null, ("$profile", profile), ("$path", path), ("$hash", hash)))
            cmd.ExecuteNonQuery();
    }

    public void SaveSummary(string profile, string path, SavedSummary summary)
    {
        using var db = Open(vectors: false);
        using var cmd = Sql(db, """
            INSERT OR IGNORE INTO summaries(profile,hash,source,summary) VALUES($profile,$hash,$source,$summary);
            INSERT INTO summary_paths(profile,path,hash) VALUES($profile,$path,$hash)
                ON CONFLICT(profile,path) DO UPDATE SET hash=excluded.hash;
            """, null, ("$profile", profile), ("$path", path), ("$hash", summary.Hash),
            ("$source", summary.Source), ("$summary", summary.Text));
        cmd.ExecuteNonQuery();
    }

    private static string? Meta(SqliteConnection db, string key, SqliteTransaction? tx = null)
    {
        using var cmd = Sql(db, "SELECT value FROM metadata WHERE key=$key", tx, ("$key", key));
        return cmd.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value, SqliteTransaction tx)
    {
        using var cmd = Sql(writer, "INSERT OR REPLACE INTO metadata VALUES($key,$value)", tx,
            ("$key", key), ("$value", value));
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Sql(SqliteConnection db, string sql, SqliteTransaction? tx = null,
        params (string Name, object Value)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    public void Dispose()
    {
        reader.Dispose();
        writer.Dispose();
    }
}

internal sealed record SavedSummary(string Hash, string Source, string Text);
