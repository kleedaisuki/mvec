using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Mvec;

internal enum OutputFormat { Auto, Text, Compact, Json }
internal enum ColorMode { Auto, Always, Never }

internal sealed partial class Output(TextWriter writer, bool compact, bool color)
{
    public static OutputFormat ParseFormat(string value) => value switch
    {
        "text" => OutputFormat.Text,
        "compact" => OutputFormat.Compact,
        "json" => OutputFormat.Json,
        _ => throw new ArgumentException("Expected --format text, compact or json.")
    };

    public static ColorMode ParseColor(string value) => value switch
    {
        "auto" => ColorMode.Auto,
        "always" => ColorMode.Always,
        "never" => ColorMode.Never,
        _ => throw new ArgumentException("Expected --color auto, always or never.")
    };

    public static void WriteCli(string method, Reply reply, OutputFormat format, ColorMode colors)
    {
        var redirected = Console.IsOutputRedirected;
        if (format == OutputFormat.Json || (format == OutputFormat.Auto && redirected))
        {
            Console.WriteLine(JsonSerializer.Serialize(reply, Wire.Default.Reply));
            return;
        }

        var writer = reply.Ok ? Console.Out : Console.Error;
        redirected = reply.Ok ? redirected : Console.IsErrorRedirected;
        var color = format != OutputFormat.Compact && CanColor(colors, redirected, reply.Ok);
        new Output(writer, format == OutputFormat.Compact, color).Write(method, reply);
    }

    public void Write(string method, Reply reply)
    {
        if (!reply.Ok)
        {
            var error = reply.Error!;
            writer.WriteLine($"{Paint("1;31", error.Code)}: {Clean(error.Message, multiline: true)}");
            return;
        }
        var result = reply.Result!.Value;
        switch (method)
        {
            case "search": Search(result.Deserialize(Wire.Default.SearchReply)!); break;
            case "status": Status(result.Deserialize(Wire.Default.DaemonStatus)!); break;
            case "summary": Summary(result.Deserialize(Wire.Default.SummaryReply)!); break;
        }
    }

    private void Search(SearchReply result)
    {
        if (result.Hits.Length == 0)
        {
            writer.WriteLine("No matching files.");
            return;
        }

        if (!compact)
            writer.WriteLine(Paint("1", $"{result.Hits.Length} {(result.Hits.Length == 1 ? "file" : "files")}")
                + Paint("2", $" · {result.Mode}"));

        for (var i = 0; i < result.Hits.Length; i++)
        {
            var hit = result.Hits[i];
            var location = FormattableString.Invariant($"{Clean(hit.Path)}:{hit.StartLine}");
            if (hit.EndLine != hit.StartLine)
                location += FormattableString.Invariant($"-{hit.EndLine}");
            if (compact)
            {
                writer.WriteLine(location);
                continue;
            }

            writer.WriteLine(FormattableString.Invariant($"  {i + 1}. {Paint("1;36", location)}  {Paint("2", hit.Score.ToString("0.000", CultureInfo.InvariantCulture))}"));
            writer.WriteLine("     " + Preview(hit.Chunk));
        }
    }

    private void Status(DaemonStatus status)
    {
        var state = !status.InitialScanComplete ? "initial scan pending" : status.Busy ? "updating" : "ready";
        writer.WriteLine($"{Paint("1;36", "Index")} · {state} · {status.Mode}");
        writer.WriteLine(FormattableString.Invariant($"{status.Files} files · {status.Chunks} chunks · {status.Vectors} vectors"));
        if (status.PendingPaths != 0)
            writer.WriteLine(FormattableString.Invariant($"{status.PendingPaths} paths queued"));
        if (status.LastError is { } error)
            writer.WriteLine($"{Paint("1;33", "Last error")}: {Clean(error, multiline: true)}");
    }

    private void Summary(SummaryReply result)
    {
        if (!compact || result.Summary is null || result.Cache is "small_change" or "superseded")
            writer.WriteLine($"{Paint("1;36", Clean(result.Path))} · {result.Cache}");
        if (result.Summary is { } summary)
        {
            if (!compact)
                writer.WriteLine();
            writer.WriteLine(Clean(summary, multiline: true).TrimEnd());
        }
        else
            writer.WriteLine("No cached summary.");
    }

    private string Paint(string sgr, string value) => color ? $"\u001b[{sgr}m{value}\u001b[0m" : value;

    private static string Preview(string chunk)
    {
        const int limit = 160;
        var builder = new StringBuilder(limit);
        Span<char> symbol = stackalloc char[2];
        var pendingSpace = false;
        foreach (var rune in chunk.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (builder.Length + rune.Utf16SequenceLength + (pendingSpace ? 1 : 0) > limit)
                return builder.Append('…').ToString();
            if (pendingSpace)
                builder.Append(' ');
            builder.Append(symbol[..rune.EncodeToUtf16(symbol)]);
            pendingSpace = false;
        }
        return builder.ToString();
    }

    private static string Clean(string text, bool multiline = false)
    {
        StringBuilder? escaped = null;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsControl(c) || (multiline && c is '\r' or '\n' or '\t'))
                continue;
            escaped ??= new StringBuilder(text.Length);
            escaped.Append(text, start, i - start).Append(@"\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            start = i + 1;
        }
        return escaped?.Append(text, start, text.Length - start).ToString() ?? text;
    }

    private static bool CanColor(ColorMode mode, bool redirected, bool stdout)
    {
        if (mode == ColorMode.Never || (mode == ColorMode.Auto && (redirected
            || Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("TERM") == "dumb")))
            return false;
        if (redirected || !OperatingSystem.IsWindows())
            return true;

        const uint virtualTerminal = 4;
        var handle = GetStdHandle(stdout ? -11 : -12);
        return GetConsoleMode(handle, out var flags) != 0
            && ((flags & virtualTerminal) != 0 || SetConsoleMode(handle, flags | virtualTerminal) != 0);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int stream);

    [LibraryImport("kernel32.dll")]
    private static partial int GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll")]
    private static partial int SetConsoleMode(nint handle, uint mode);
}
