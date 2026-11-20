using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using Tomlyn;
using Tomlyn.Serialization;

namespace Mvec;

internal sealed class Config
{
    [TomlPropertyName("embedding")] public EmbeddingConfig Embedding { get; set; } = new();
    [TomlPropertyName("search")] public SearchConfig Search { get; set; } = new();
    [TomlPropertyName("index")] public IndexConfig Index { get; set; } = new();
    [TomlPropertyName("summary")] public SummaryConfig Summary { get; set; } = new();

    public static Config Load(Project project)
    {
        var config = TomlSerializer.Deserialize(File.ReadAllText(project.ConfigPath), ConfigToml.Default.Config)
            ?? throw new InvalidDataException("The project configuration is empty.");
        var e = config.Embedding;
        if (e.Provider is not ("openrouter" or "voyage") || string.IsNullOrWhiteSpace(e.Model)
            || e.Dimensions is < 1 or > 16384 || e.BatchSize is < 1 or > 128)
            throw new InvalidDataException("Invalid [embedding] provider, model, dimensions or batch_size.");
        if (e.Url.Scheme is not ("http" or "https") || (e.Url.Scheme == "http" && !e.Url.IsLoopback)
            || (e.Provider == "openrouter" && string.IsNullOrWhiteSpace(e.Route)))
            throw new InvalidDataException("Use an HTTPS embedding endpoint (HTTP is allowed on loopback); OpenRouter requires a route.");
        if (e.QueryModel is not null &&
            (string.IsNullOrWhiteSpace(e.QueryModel) || (e.QueryModel != e.Model &&
                !(e.Dimensions is 256 or 512 or 1024 or 2048 && e.IsVoyage4(e.Model) && e.IsVoyage4(e.QueryModel)))))
            throw new InvalidDataException("Distinct [embedding].query_model is supported only within the Voyage 4 space.");
        var s = config.Search;
        if (s.Mode is not ("exact" or "ann") || s.TrainMinVectors is < 16 or > 100_000
            || !(s.Probe > 0 && s.Probe <= 1) || s.Candidates is < 1 or > 32)
            throw new InvalidDataException("Invalid [search] mode, train_min_vectors, probe or candidates.");
        var i = config.Index;
        if (i.ChunkChars is < 128 or > 16000 || i.MaxFileBytes is < 128 or > 134_217_728
            || i.ReconcileSeconds is < 1 or > 86400)
            throw new InvalidDataException("Invalid [index] chunk_chars, max_file_bytes or reconcile_seconds.");
        if (string.IsNullOrWhiteSpace(config.Summary.Model) || config.Summary.MaxChars is < 128 or > 1_000_000
            || !(config.Summary.ChangeThreshold >= 0 && config.Summary.ChangeThreshold <= 1))
            throw new InvalidDataException("Invalid [summary] model, max_chars or change_threshold.");
        return config;
    }

    public const string Template = """
        [embedding]
        provider = "openrouter"
        model = "mongodb/voyage-code-4"
        dimensions = 1024
        route = "voyageai"
        batch_size = 32

        [search]
        mode = "exact"
        train_min_vectors = 5000
        probe = 0.10
        candidates = 4

        [index]
        chunk_chars = 1800
        max_file_bytes = 2097152
        reconcile_seconds = 300
        exclude = []

        [summary]
        enabled = false
        model = "gpt-6.1-sol"
        change_threshold = 0.15
        max_chars = 80000
        """;
}

internal sealed class EmbeddingConfig
{
    [TomlPropertyName("provider")] public string Provider { get; set; } = "openrouter";
    [TomlPropertyName("model")] public string Model { get; set; } = "mongodb/voyage-code-4";
    [TomlPropertyName("query_model")] public string? QueryModel { get; set; }
    [TomlPropertyName("dimensions")] public int Dimensions { get; set; } = 1024;
    [TomlPropertyName("route")] public string? ConfiguredRoute { get; set; }
    public string Route => ConfiguredRoute ?? (IsMongoDbModel(Model) ? "voyageai"
        : Model.StartsWith("openai/", StringComparison.Ordinal) ? "openai" : "");
    [TomlPropertyName("batch_size")] public int BatchSize { get; set; } = 32;
    [TomlPropertyName("endpoint")] public string? Endpoint { get; set; }
    [TomlPropertyName("key_env")] public string? KeyEnv { get; set; }

    public Uri Url => new(Endpoint ?? (Provider == "voyage"
        ? "https://api.voyageai.com/v1/embeddings" : "https://openrouter.ai/api/v1/embeddings"));
    public string KeyVariable => KeyEnv ?? (Provider == "voyage" ? "VOYAGE_API_KEY" : "OPENROUTER_API_KEY");
    public bool IsVoyage4(string model)
    {
        var name = Provider == "voyage" ? model
            : Route == "voyageai" && IsMongoDbModel(model) ? model[(model.IndexOf('/') + 1)..] : "";
        return name is "voyage-4" or "voyage-4-lite" or "voyage-4-large";
    }

    private static bool IsMongoDbModel(string model) => model.StartsWith("mongodb/", StringComparison.Ordinal)
        || model.StartsWith("voyageai/", StringComparison.Ordinal);

    public bool UsesVoyageInputType => Provider == "voyage" || (Route == "voyageai" && IsMongoDbModel(Model));

    public string Space => Hash.OfText(FormattableString.Invariant(
        $"v2\n{Provider}\n{Url.AbsoluteUri}\n{Model}\n{Dimensions}\n{(Provider == "openrouter" ? Route : "")}"));
}

internal sealed class SearchConfig
{
    [TomlPropertyName("mode")] public string Mode { get; set; } = "exact";
    [TomlPropertyName("train_min_vectors")] public int TrainMinVectors { get; set; } = 5000;
    [TomlPropertyName("probe")] public double Probe { get; set; } = .10;
    [TomlPropertyName("candidates")] public int Candidates { get; set; } = 4;
}

internal sealed class IndexConfig
{
    [TomlPropertyName("chunk_chars")] public int ChunkChars { get; set; } = 1800;
    [TomlPropertyName("max_file_bytes")] public int MaxFileBytes { get; set; } = 2 * 1024 * 1024;
    [TomlPropertyName("reconcile_seconds")] public int ReconcileSeconds { get; set; } = 300;
    [TomlPropertyName("exclude")] public string[] Exclude { get; set; } = [];
}

internal sealed class SummaryConfig
{
    [TomlPropertyName("enabled")] public bool Enabled { get; set; }
    [TomlPropertyName("model")] public string Model { get; set; } = "gpt-6.1-sol";
    [TomlPropertyName("change_threshold")] public double ChangeThreshold { get; set; } = .15;
    [TomlPropertyName("max_chars")] public int MaxChars { get; set; } = 80_000;

    public string Profile => Hash.OfText($"chatgpt-responses/v1\n{Model}");
}

[TomlSerializable(typeof(Config))]
internal partial class ConfigToml : TomlSerializerContext;

internal static class Hash
{
    public static string OfBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string OfText(string text) => OfBytes(Encoding.UTF8.GetBytes(text));
}

internal sealed class Project
{
    private static readonly string[] Excluded = [".mvec", ".git", ".hg", ".svn", ".temp", "node_modules", "bin", "obj", ".env", ".env.*", "*.env"];
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public string Root { get; }
    public string State => Path.Join(Root, ".mvec");
    public string ConfigPath => Path.Join(State, "config.toml");
    public string Socket => Path.Join(State, "mvec.sock");
    public string Pipe => "mvec-" + Hash.OfText(Root.ToUpperInvariant());

    public Project(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root))
            throw new DirectoryNotFoundException(Root);
        // A direct alias to the project root must address the same daemon.
        if (Directory.ResolveLinkTarget(Root, true) is { } target)
            Root = Path.TrimEndingDirectorySeparator(target.FullName);
    }

    public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath)
        .Replace(Path.DirectorySeparatorChar, '/');

    public string Resolve(string path)
    {
        var absolute = Path.GetFullPath(path, Root);
        var relative = Relative(absolute);
        if (relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException("Pass a file path inside the project.");
        return absolute;
    }

    public bool Includes(string path, IndexConfig config)
    {
        foreach (var component in path.Split('/'))
        {
            if (Excluded.Concat(config.Exclude).Any(pattern =>
                FileSystemName.MatchesSimpleExpression(pattern, component, OperatingSystem.IsWindows())))
                return false;
        }
        return true;
    }

    public bool IsLink(string relative)
    {
        var path = Root;
        foreach (var part in relative.Split('/'))
        {
            path = Path.Join(path, part);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }
}
