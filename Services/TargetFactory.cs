using System.IO;
using DevStarter.Models;

namespace DevStarter.Services;

public static class TargetFactory
{
    static readonly string[] ComposeNames = { "compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml" };

    public static LaunchTarget Unity(string dir) => new() { Kind = TargetKind.Unity, Path = dir };
    public static LaunchTarget VsCode(string path) => new() { Kind = TargetKind.VsCode, Path = path };
    public static LaunchTarget Blender(string file) => new() { Kind = TargetKind.Blender, Path = file };
    public static LaunchTarget Docker(string composeOrDir) => new() { Kind = TargetKind.Docker, Path = composeOrDir };
    public static LaunchTarget Folder(string dir) => new() { Kind = TargetKind.Folder, Path = dir };
    public static LaunchTarget Terminal(string dir) => new() { Kind = TargetKind.Terminal, Path = dir };

    public static bool IsUnityProject(string dir)
        => File.Exists(Path.Combine(dir, "ProjectSettings", "ProjectVersion.txt"));

    public static string? FindCompose(string dir)
    {
        foreach (var name in ComposeNames)
        {
            var f = Path.Combine(dir, name);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    // Bir klasör için önerilen hedefler: Unity projesiyse Unity + VS Code, compose varsa Docker
    public static List<LaunchTarget> ForFolder(string dir)
    {
        var list = new List<LaunchTarget>();
        if (IsUnityProject(dir)) list.Add(Unity(dir));
        list.Add(VsCode(dir));
        var compose = FindCompose(dir);
        if (compose != null) list.Add(Docker(compose));
        return list;
    }

    // Sürüklenen/ seçilen dosyanın türünü uzantısından tahmin eder
    public static LaunchTarget FromFile(string file)
    {
        var name = Path.GetFileName(file).ToLowerInvariant();
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ComposeNames.Contains(name) || (name.Contains("compose") && ext is ".yml" or ".yaml"))
            return Docker(file);
        return ext switch
        {
            ".blend" => Blender(file),
            ".sln" or ".slnx" => new LaunchTarget { Kind = TargetKind.VisualStudio, Path = file },
            ".code-workspace" => VsCode(file),
            ".exe" or ".lnk" or ".bat" or ".cmd" => new LaunchTarget { Kind = TargetKind.Custom, Path = file },
            _ => new LaunchTarget { Kind = TargetKind.Url, Path = file }
        };
    }

    public static Project ProjectFromPath(string path)
    {
        if (Directory.Exists(path))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            return NewProject(name.Length > 0 ? name : path, ForFolder(path));
        }
        return NewProject(Path.GetFileNameWithoutExtension(path), new List<LaunchTarget> { FromFile(path) });
    }

    public static Project NewProject(string name, IEnumerable<LaunchTarget> targets)
    {
        var p = new Project { Name = name, Color = Palette.ForName(name) };
        foreach (var t in targets) p.Targets.Add(t);
        return p;
    }

    public static string SuggestName(LaunchTarget t)
    {
        if (t.Label.Length > 0) return t.Label;
        if (t.Kind == TargetKind.Chrome) return "Chrome";
        if (t.Path.Length == 0 || t.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return t.KindName;
        if (Directory.Exists(t.Path)) return Path.GetFileName(Path.TrimEndingDirectorySeparator(t.Path));
        if (t.Kind == TargetKind.Docker && File.Exists(t.Path))
            return Path.GetFileName(Path.GetDirectoryName(t.Path) ?? t.Path);
        return Path.GetFileNameWithoutExtension(t.Path);
    }

    // AppCatalog'dan gelen yolu başlatılabilir hale getirir
    public static string NormalizeAppPath(string raw)
    {
        if (raw.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return raw;
        if (File.Exists(raw)) return raw;
        return @"shell:AppsFolder\" + raw;
    }
}
