using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Mvec;

internal sealed record Request(string Method, string? Query = null, int? TopK = null, string? Mode = null,
    string? Path = null, bool CacheOnly = false);
internal sealed record Reply(bool Ok, JsonElement? Result = null, RpcError? Error = null)
{
    public static Reply From<T>(T value, JsonTypeInfo<T> type) => new(true, JsonSerializer.SerializeToElement(value, type));
    public static Reply Fail(string code, string message) => new(false, null, new(code, message));
}
internal sealed record RpcError(string Code, string Message);
internal sealed record SearchReply(string Mode, SearchHit[] Hits);
internal sealed record SearchHit(string Path, double Score, string Chunk, int StartOffset, int EndOffset, int StartLine, int EndLine);
internal sealed record DaemonStatus(long Files, long Chunks, long Vectors, string Mode, string Vec1, bool InitialScanComplete,
    bool Busy, int PendingPaths, string? LastError);
internal sealed record SummaryReply(string Path, string? DocumentHash, string? SummaryHash, string? Summary, string Cache);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Reply))]
[JsonSerializable(typeof(SearchReply))]
[JsonSerializable(typeof(DaemonStatus))]
[JsonSerializable(typeof(SummaryReply))]
[JsonSerializable(typeof(VoyageRequest))]
[JsonSerializable(typeof(RouterRequest))]
[JsonSerializable(typeof(EmbeddingResponse))]
internal partial class Wire : JsonSerializerContext;
