using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Mvec;

internal static class Mcp
{
    private const string Version = "2025-11-25";
    private const string Tools = """
        {"tools":[
          {"name":"mvec_search","description":"Find project files by semantic relevance.","inputSchema":{"type":"object","properties":{"query":{"type":"string"},"top_k":{"type":"integer","minimum":1,"maximum":100},"mode":{"enum":["exact","ann"]}},"required":["query"]}},
          {"name":"mvec_summary","description":"Get a document summary. Generates on cache miss unless cache_only is true.","inputSchema":{"type":"object","properties":{"path":{"type":"string"},"cache_only":{"type":"boolean"}},"required":["path"]}},
          {"name":"mvec_status","description":"Get the project indexing status.","inputSchema":{"type":"object","properties":{}}}
        ]}
        """;

    public static async Task RunAsync(Project project, CancellationToken ct)
    {
        while (await Console.In.ReadLineAsync(ct) is { } line)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { Write(default, "error", Error(-32700, "Parse error")); continue; }

            using (doc)
            {
                var message = doc.RootElement;
                if (!TryProperty(message, "method", out var method) || method.ValueKind != JsonValueKind.String
                    || !TryProperty(message, "jsonrpc", out var jsonrpc) || jsonrpc.ValueKind != JsonValueKind.String
                    || jsonrpc.GetString() != "2.0")
                {
                    Write(default, "error", Error(-32600, "Invalid request"));
                    continue;
                }
                if (!message.TryGetProperty("id", out var id))
                    continue;
                TryProperty(message, "params", out var parameters);

                try
                {
                    var result = method.GetString() switch
                    {
                        "initialize" => Initialize(parameters),
                        "ping" => "{}",
                        "tools/list" => Tools,
                        "tools/call" => await CallAsync(project, parameters, ct),
                        _ => null
                    };
                    if (result is null)
                        Write(id, "error", Error(-32601, "Method not found"));
                    else
                        Write(id, "result", result);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                {
                    Write(id, "error", Error(-32602, "Invalid params"));
                }
            }
        }
    }

    private static string Initialize(JsonElement parameters)
    {
        var offered = TryProperty(parameters, "protocolVersion", out var version) ? version.GetString() : null;
        var selected = offered is "2024-11-05" or "2025-03-26" or "2025-06-18" ? offered : Version;
        return $$$"""{"protocolVersion":"{{{selected}}}","capabilities":{"tools":{}},"serverInfo":{"name":"mvec","version":"0.1.0"}}""";
    }

    private static async Task<string> CallAsync(Project project, JsonElement parameters, CancellationToken ct)
    {
        var name = parameters.GetProperty("name").GetString();
        TryProperty(parameters, "arguments", out var args);
        var method = name switch
        {
            "mvec_search" => "search",
            "mvec_summary" => "summary",
            "mvec_status" => "status",
            _ => throw new ArgumentException("Unknown tool.")
        };

        var request = args.ValueKind == JsonValueKind.Undefined
            ? new Request(method)
            : (args.Deserialize(Wire.Default.Request) ?? throw new JsonException()).WithMethod(method);
        Reply response;
        try { response = await Ipc.CallAsync(project, request, ct); }
        catch (IOException ex) { response = Reply.Fail("daemon_unavailable", ex.Message); }
        var text = response.Ok ? response.Result!.Value.GetRawText() : $"{response.Error!.Code}: {response.Error.Message}";
        return MakeJson(writer =>
        {
            writer.WriteStartObject();
            if (!response.Ok)
                writer.WriteBoolean("isError", true);
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static Request WithMethod(this Request request, string method) => request with { Method = method };

    private static bool TryProperty(JsonElement element, string name, out JsonElement property)
    {
        property = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out property);
    }

    private static string Error(int code, string message) => MakeJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
    });

    private static void Write(JsonElement id, string key, string value) => Console.WriteLine(MakeJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        if (id.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            id.WriteTo(writer);
        else
            writer.WriteNullValue();
        writer.WritePropertyName(key);
        writer.WriteRawValue(value);
        writer.WriteEndObject();
    }));

    private static string MakeJson(Action<Utf8JsonWriter> emit)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        emit(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
