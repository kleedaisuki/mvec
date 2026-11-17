using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Mvec;

internal sealed class Embeddings(HttpClient http, EmbeddingConfig config)
{
    public int BatchSize => config.BatchSize;

    public async Task<byte[][]> GetAsync(string[] texts, bool query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config.Url);
        if (Environment.GetEnvironmentVariable(config.KeyVariable) is { Length: > 0 } key)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        else if (!config.Url.IsLoopback)
            throw new InvalidOperationException($"Set {config.KeyVariable} for the {config.Provider} embedding provider.");

        var model = query ? config.QueryModel ?? config.Model : config.Model;
        request.Content = config.Provider == "voyage"
            ? JsonContent.Create(new VoyageRequest(model, texts, config.Dimensions, query ? "query" : "document"),
                Wire.Default.VoyageRequest)
            : JsonContent.Create(new RouterRequest(model, texts, config.Dimensions, new([config.Route]),
                config.UsesVoyageInputType ? query ? "search_query" : "search_document" : null),
                Wire.Default.RouterRequest);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{config.Provider} embedding request returned HTTP {(int)response.StatusCode}.",
                null, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync(Wire.Default.EmbeddingResponse, ct))?.Data;
        if (data?.Length != texts.Length)
            throw new InvalidDataException($"{config.Provider} returned the wrong number of embeddings.");

        var results = new byte[texts.Length][];
        foreach (var item in data)
        {
            var vector = item.Embedding;
            if (item.Index < 0 || item.Index >= results.Length || results[item.Index] is not null
                || vector?.Length != config.Dimensions)
                throw new InvalidDataException($"{config.Provider} returned an invalid embedding index or dimension.");

            double squaredNorm = 0;
            foreach (var value in vector)
                squaredNorm += (double)value * value;
            if (!double.IsFinite(squaredNorm) || squaredNorm == 0)
                throw new InvalidDataException($"{config.Provider} returned a non-finite or zero embedding.");

            var norm = Math.Sqrt(squaredNorm);
            for (var i = 0; i < vector.Length; i++)
                vector[i] = (float)(vector[i] / norm);
            results[item.Index] = MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
        }
        return results;
    }
}

internal sealed record VoyageRequest(string Model, string[] Input, int OutputDimension, string InputType)
{
    public bool Truncation => false;
}
internal sealed record RouterRequest(string Model, string[] Input, int Dimensions, RouterProvider Provider, string? InputType)
{
    public string EncodingFormat => "float";
}
internal sealed record RouterProvider(string[] Only)
{
    public bool AllowFallbacks => false;
    public bool RequireParameters => true;
}
internal sealed record EmbeddingResponse(EmbeddingItem[] Data);
internal sealed record EmbeddingItem(int Index, float[] Embedding);
