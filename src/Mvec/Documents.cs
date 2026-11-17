using System.Text;

namespace Mvec;

internal sealed record Document(string Path, string Hash, string Text)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16Le = new(false, false, true);
    private static readonly UnicodeEncoding Utf16Be = new(true, false, true);

    public static Document? Read(Project project, string relative, int maxBytes)
    {
        var fullPath = project.Resolve(relative);
        try
        {
            if (project.IsLink(relative) || (File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
                return null;
            using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16_384, FileOptions.SequentialScan);
            if (file.Length > maxBytes)
                return null;
            var bytes = new byte[(int)file.Length];
            if (file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) != bytes.Length || file.ReadByte() != -1)
                throw new IOException($"File changed while reading: {relative}");
            var text = Decode(bytes);
            return text is null ? null : new(relative, Mvec.Hash.OfBytes(bytes), text);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string? Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var text = bytes switch
            {
                [0xef, 0xbb, 0xbf, ..] => Utf8.GetString(bytes[3..]),
                [0xff, 0xfe, ..] => Utf16Le.GetString(bytes[2..]),
                [0xfe, 0xff, ..] => Utf16Be.GetString(bytes[2..]),
                _ => Utf8.GetString(bytes)
            };
            return text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f')) ? null : text;
        }
        catch (DecoderFallbackException) { return null; }
    }

    public IEnumerable<Chunk> Chunks(int size)
    {
        var line = 1;
        for (var start = 0; start < Text.Length;)
        {
            var end = start + Math.Min(size, Text.Length - start);
            if (end < Text.Length)
            {
                var newline = Text.LastIndexOfAny(['\r', '\n'], end - 1, end - start);
                if (newline >= start + size / 2)
                    end = newline + 1;
                if (end < Text.Length && (char.IsLowSurrogate(Text[end]) || (Text[end - 1] == '\r' && Text[end] == '\n')))
                    end++;
            }
            var text = Text[start..end];
            var startLine = line;
            var endLine = line;
            for (var j = 0; j < text.Length; j++)
            {
                endLine = line;
                if (text[j] == '\n' || (text[j] == '\r' && (j + 1 == text.Length || text[j + 1] != '\n')))
                    line++;
            }
            if (!string.IsNullOrWhiteSpace(text))
                yield return new(Mvec.Hash.OfText(text), text, start, end, startLine, endLine);
            start = end;
        }
    }
}

internal sealed record Chunk(string Hash, string Text, int StartOffset, int EndOffset, int StartLine, int EndLine);
