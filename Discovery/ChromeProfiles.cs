using System.IO;
using System.Text.Json;
using DevStarter.Services;

namespace DevStarter.Discovery;

public record ChromeProfile(string Dir, string Name)
{
    public string Display => Name.Length == 0 || Name == Dir ? Dir : $"{Name}  ({Dir})";
}

// Chrome profilleri: User Data\Local State → profile.info_cache
public static class ChromeProfiles
{
    static List<ChromeProfile>? cache;

    public static List<ChromeProfile> Load()
    {
        if (cache != null) return cache;
        var list = new List<ChromeProfile>();
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data", "Local State");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("profile", out var profile)
                    && profile.TryGetProperty("info_cache", out var info)
                    && info.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in info.EnumerateObject())
                    {
                        var name = p.Value.ValueKind == JsonValueKind.Object
                            && p.Value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString() ?? "" : "";
                        list.Add(new ChromeProfile(p.Name, name));
                    }
                }
            }
        }
        catch (Exception ex) { Log.Error(ex, "Chrome profiles"); }

        cache = list
            .OrderBy(p => p.Dir == "Default" ? 0 : 1)
            .ThenBy(p => p.Dir.Length)
            .ThenBy(p => p.Dir, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return cache;
    }

    public static string NameOf(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return "yeni pencere";
        var p = Load().FirstOrDefault(x => x.Dir == dir);
        return p == null || p.Name.Length == 0 ? dir : p.Name;
    }
}
