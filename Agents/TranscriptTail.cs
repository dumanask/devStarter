using System.IO;
using System.Text;
using System.Text.Json;

namespace DevStarter.Agents;

// Çok büyüyebilen (GB'larca) JSONL transcript dosyalarının yalnızca sonunu okur
static class TranscriptTail
{
    public static List<string> ReadLines(string path, int maxBytes)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[fs.Length - start];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = fs.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }

        int offset = 0;
        if (start > 0)
        {
            // Yarım kalan ilk satırı atla
            int nl = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (nl < 0) return lines;
            offset = nl + 1;
        }
        var text = Encoding.UTF8.GetString(buffer, offset, read - offset);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0) lines.Add(trimmed);
        }
        return lines;
    }

    public static JsonDocument? TryParse(string line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
    }

    public static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    public static string OneLine(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length > max ? s[..(max - 1)] + "…" : s;
    }
}
