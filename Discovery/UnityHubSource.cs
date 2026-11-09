using System.IO;
using System.Text.Json;
using DevStarter.Services;

namespace DevStarter.Discovery;

public record UnityHubProject(string Title, string Path, string Version, bool IsFavorite, DateTime LastModified);

// Unity Hub proje listesi.
// Yeni Hub: %APPDATA%\UnityHub\hub.db (SQLite, "projects" tablosu: path, data JSON)
// Eski Hub: %APPDATA%\UnityHub\projects-v1.json (yeni sürümde güncellenmiyor, sadece yedek)
public static class UnityHubSource
{
    static string HubDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub");

    public static List<UnityHubProject> Load()
    {
        var list = FromDatabase();
        if (list.Count == 0) list = FromJson();
        return list
            .Where(p => Directory.Exists(p.Path))
            .OrderByDescending(p => p.IsFavorite)
            .ThenByDescending(p => p.LastModified)
            .ToList();
    }

    static List<UnityHubProject> FromDatabase()
    {
        var list = new List<UnityHubProject>();
        try
        {
            var db = System.IO.Path.Combine(HubDir, "hub.db");
            if (!File.Exists(db)) return list;
            foreach (var row in WinSqlite.Query(db, "SELECT path, data FROM projects", 2))
            {
                try
                {
                    using var doc = JsonDocument.Parse(row[1]);
                    if (Parse(doc.RootElement, row[0]) is UnityHubProject p) list.Add(p);
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Error(ex, "UnityHub hub.db"); }
        return list;
    }

    static List<UnityHubProject> FromJson()
    {
        var list = new List<UnityHubProject>();
        try
        {
            var file = System.IO.Path.Combine(HubDir, "projects-v1.json");
            if (!File.Exists(file)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return list;
            foreach (var prop in data.EnumerateObject())
                if (Parse(prop.Value, prop.Name) is UnityHubProject p) list.Add(p);
        }
        catch (Exception ex) { Log.Error(ex, "UnityHub projects-v1.json"); }
        return list;
    }

    static UnityHubProject? Parse(JsonElement v, string fallbackPath)
    {
        if (v.ValueKind != JsonValueKind.Object) return null;
        var path = Str(v, "path") ?? fallbackPath;
        if (string.IsNullOrWhiteSpace(path)) return null;

        var title = Str(v, "title") ?? System.IO.Path.GetFileName(path);
        var version = Str(v, "version") ?? "";
        bool fav = v.TryGetProperty("isFavorite", out var f) && f.ValueKind == JsonValueKind.True;

        DateTime modified = DateTime.MinValue;
        if (v.TryGetProperty("lastModified", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetDouble(out var ms))
            modified = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;

        return new UnityHubProject(title, path, version, fav, modified);
    }

    static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
