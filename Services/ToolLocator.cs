using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DevStarter.Services;

// Programların exe yollarını bulur: kullanıcı ayarı → App Paths kaydı → bilinen konumlar → PATH
public static class ToolLocator
{
    public const string Code = "code";
    public const string Chrome = "chrome";
    public const string Blender = "blender";
    public const string DockerDesktop = "docker-desktop";
    public const string Docker = "docker";
    public const string UnityHub = "unity-hub";
    public const string Terminal = "wt";
    public const string VisualStudio = "devenv";
    public const string PowerShell = "powershell";
    public const string Explorer = "explorer";

    public static IDictionary<string, string> Overrides { get; set; } = new Dictionary<string, string>();

    static readonly ConcurrentDictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Find(string tool)
    {
        if (Overrides.TryGetValue(tool, out var o) && File.Exists(o)) return o;
        return cache.GetOrAdd(tool, Locate);
    }

    static string? Locate(string tool) => tool switch
    {
        Code => FirstExisting(AppPath("Code.exe"), Local(@"Programs\Microsoft VS Code\Code.exe"), Pf(@"Microsoft VS Code\Code.exe")),
        Chrome => FirstExisting(AppPath("chrome.exe"), Pf(@"Google\Chrome\Application\chrome.exe"),
                                Pf86(@"Google\Chrome\Application\chrome.exe"), Local(@"Google\Chrome\Application\chrome.exe")),
        Blender => FirstExisting(AppPath("blender.exe"), LatestBlender()),
        DockerDesktop => FirstExisting(Pf(@"Docker\Docker\Docker Desktop.exe")),
        Docker => FirstExisting(Pf(@"Docker\Docker\resources\bin\docker.exe"), OnPath("docker.exe")),
        UnityHub => FirstExisting(Pf(@"Unity Hub\Unity Hub.exe")),
        Terminal => FirstExisting(Local(@"Microsoft\WindowsApps\wt.exe"), OnPath("wt.exe")),
        VisualStudio => FirstExisting(AppPath("devenv.exe")),
        PowerShell => FirstExisting(OnPath("pwsh.exe"),
                                    Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")),
        Explorer => FirstExisting(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")),
        _ => null
    };

    // ---- Unity ----

    public static string? UnityEditor(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        foreach (var root in UnityEditorRoots())
        {
            var exe = Path.Combine(root, version, "Editor", "Unity.exe");
            if (File.Exists(exe)) return exe;
        }
        foreach (var (ver, exe) in HubEditorEntries())
            if (string.Equals(ver, version, StringComparison.OrdinalIgnoreCase) && File.Exists(exe))
                return exe;
        return null;
    }

    public static string? AnyUnityEditor()
    {
        foreach (var root in UnityEditorRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var exe = Path.Combine(dir, "Editor", "Unity.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    static IEnumerable<string> UnityEditorRoots()
    {
        yield return Pf(@"Unity\Hub\Editor");
        var secondary = ReadHubJsonString("secondaryInstallPath.json");
        if (!string.IsNullOrWhiteSpace(secondary)) yield return secondary;
    }

    static string? ReadHubJsonString(string file)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub", file);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
        }
        catch { return null; }
    }

    // Hub'a elle eklenmiş editörler (editors-v2.json)
    static IEnumerable<(string Version, string Exe)> HubEditorEntries()
    {
        var result = new List<(string, string)>();
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub", "editors-v2.json");
            if (!File.Exists(path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
            foreach (var e in data.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var ver = e.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                if (!e.TryGetProperty("location", out var loc)) continue;
                if (loc.ValueKind == JsonValueKind.String) result.Add((ver, loc.GetString() ?? ""));
                else if (loc.ValueKind == JsonValueKind.Array)
                    foreach (var l in loc.EnumerateArray())
                        if (l.ValueKind == JsonValueKind.String) result.Add((ver, l.GetString() ?? ""));
            }
        }
        catch { }
        return result;
    }

    // ---- Varsayılan tarayıcı ----

    public static string? DefaultBrowser()
    {
        return cache.GetOrAdd("default-browser", _ =>
        {
            try
            {
                using var choice = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
                var progId = choice?.GetValue("ProgId") as string;
                if (string.IsNullOrEmpty(progId)) return null;
                using var cmd = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                var line = cmd?.GetValue(null) as string;
                if (string.IsNullOrEmpty(line)) return null;
                line = line.Trim();
                string exe = line.StartsWith('"')
                    ? line[1..Math.Max(1, line.IndexOf('"', 1))]
                    : line.Split(' ')[0];
                return File.Exists(exe) ? exe : null;
            }
            catch { return null; }
        });
    }

    // ---- yardımcılar ----

    static string? LatestBlender()
    {
        try
        {
            var root = Pf("Blender Foundation");
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateDirectories(root)
                .Select(d => (Dir: d, Ver: ParseVersion(Path.GetFileName(d))))
                .OrderByDescending(x => x.Ver)
                .Select(x => Path.Combine(x.Dir, "blender.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch { return null; }
    }

    static Version ParseVersion(string name)
    {
        var digits = new string(name.SkipWhile(c => !char.IsDigit(c)).ToArray());
        return Version.TryParse(digits, out var v) ? v : new Version(0, 0);
    }

    static string? AppPath(string exeName)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
                if (key?.GetValue(null) is string s && s.Length > 0)
                    return Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
            }
            catch { }
        }
        return null;
    }

    static string? OnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim().Trim('"'), exeName);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    static string? FirstExisting(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));

    static string Pf(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), rel);
    static string Pf86(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), rel);
    static string Local(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), rel);
}
