using System.IO;

namespace DevStarter.Discovery;

// Blender son dosyalar: %APPDATA%\Blender Foundation\Blender\<sürüm>\config\recent-files.txt
public static class BlenderRecent
{
    public static List<string> Load()
    {
        var result = new List<string>();
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Blender Foundation", "Blender");
            if (!Directory.Exists(root)) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var verDir in Directory.EnumerateDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(verDir, "config", "recent-files.txt");
                if (!File.Exists(file)) continue;
                foreach (var line in File.ReadLines(file))
                {
                    var p = line.Trim();
                    if (p.Length > 0 && File.Exists(p) && seen.Add(p)) result.Add(p);
                }
            }
        }
        catch { }
        return result.Take(15).ToList();
    }
}
