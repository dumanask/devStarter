using System.IO;
using System.Text.Json;
using DevStarter.Services;

namespace DevStarter.Discovery;

public record ChromeBookmark(string ProfileDir, string ProfileName, string Name, string Url, string Folder);

// Klasör: içindeki sayfalar tek pencerede sekme olarak açılır
public record ChromeBookmarkFolder(string ProfileDir, string ProfileName, string Name, string Folder, List<string> Urls);

// Chrome yer imleri (yıldızlananlar): User Data\<profil>\Bookmarks (JSON)
public static class ChromeBookmarks
{
    public static (List<ChromeBookmark> Bookmarks, List<ChromeBookmarkFolder> Folders) Load()
    {
        var bookmarks = new List<ChromeBookmark>();
        var folders = new List<ChromeBookmarkFolder>();
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data");

        foreach (var profile in ChromeProfiles.Load())
        {
            var file = Path.Combine(userData, profile.Dir, "Bookmarks");
            if (!File.Exists(file)) continue;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                if (!doc.RootElement.TryGetProperty("roots", out var roots) || roots.ValueKind != JsonValueKind.Object) continue;

                var profileName = profile.Name.Length > 0 ? profile.Name : profile.Dir;
                foreach (var root in roots.EnumerateObject())
                {
                    if (root.Value.ValueKind != JsonValueKind.Object) continue;
                    var rootName = root.Name switch
                    {
                        "bookmark_bar" => "Yer imleri çubuğu",
                        "other" => "Diğer yer imleri",
                        "synced" => "Mobil yer imleri",
                        _ => Str(root.Value, "name") ?? root.Name
                    };
                    Walk(root.Value, rootName, profile.Dir, profileName, bookmarks, folders, isRoot: true);
                }
            }
            catch (Exception ex) { Log.Error(ex, $"Chrome bookmarks {profile.Dir}"); }
        }
        return (bookmarks, folders);
    }

    static void Walk(JsonElement node, string path, string dir, string profileName,
                     List<ChromeBookmark> bookmarks, List<ChromeBookmarkFolder> folders, bool isRoot)
    {
        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return;

        var directUrls = new List<string>();
        foreach (var child in children.EnumerateArray())
        {
            var type = Str(child, "type");
            var name = Str(child, "name") ?? "";
            if (type == "url")
            {
                var url = Str(child, "url");
                if (string.IsNullOrEmpty(url) || url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                bookmarks.Add(new ChromeBookmark(dir, profileName, name.Length > 0 ? name : url, url, path));
                directUrls.Add(url);
            }
            else if (type == "folder")
            {
                Walk(child, $"{path} › {name}", dir, profileName, bookmarks, folders, isRoot: false);
            }
        }

        // Kök klasörleri (çubuk, diğer) "hepsini aç" olarak önermek çok kalabalık olur
        if (!isRoot && directUrls.Count > 0)
        {
            var folderName = path.Contains(" › ") ? path[(path.LastIndexOf(" › ", StringComparison.Ordinal) + 3)..] : path;
            folders.Add(new ChromeBookmarkFolder(dir, profileName, folderName, path, directUrls));
        }
    }

    static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
