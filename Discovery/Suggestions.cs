using System.IO;
using DevStarter.Models;
using DevStarter.Services;

namespace DevStarter.Discovery;

public record ProjectSuggestion(string Name, string Group, string Subtitle, List<LaunchTarget> Targets);

// Unity Hub ve VS Code geçmişinden "tek tıkla eklenebilir" proje önerileri üretir.
// Arka plan iş parçacığında çalışabilir (UI nesnesi oluşturmaz).
public static class Suggestions
{
    public const string GroupMatched = "Önerilen — Unity + VS Code";
    public const string GroupUnity = "Unity Hub projeleri";
    public const string GroupVsCode = "VS Code klasörleri";

    public static List<ProjectSuggestion> Build(IEnumerable<string> alreadyUsedPaths)
    {
        var used = new HashSet<string>(alreadyUsedPaths.Select(Norm).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);
        var vsFolders = VsCodeSource.Load();
        var vsSet = new HashSet<string>(vsFolders.Select(v => Norm(v.Path)), StringComparer.OrdinalIgnoreCase);

        var matched = new List<ProjectSuggestion>();
        var unityOnly = new List<ProjectSuggestion>();
        var vsOnly = new List<ProjectSuggestion>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var u in UnityHubSource.Load())
        {
            var key = Norm(u.Path);
            if (used.Contains(key) || !taken.Add(key)) continue;

            var targets = TargetFactory.ForFolder(u.Path);
            if (!targets.Any(t => t.Kind == TargetKind.Unity)) targets.Insert(0, TargetFactory.Unity(u.Path));

            bool inVsCode = vsSet.Contains(key);
            var subtitle = $"{Describe(targets, u.Version)}  ·  {u.Path}";
            (inVsCode ? matched : unityOnly).Add(new ProjectSuggestion(u.Title, inVsCode ? GroupMatched : GroupUnity, subtitle, targets));
        }

        foreach (var v in vsFolders)
        {
            var key = Norm(v.Path);
            if (used.Contains(key) || !taken.Add(key)) continue;
            var targets = v.IsWorkspaceFile ? new List<LaunchTarget> { TargetFactory.VsCode(v.Path) } : TargetFactory.ForFolder(v.Path);
            vsOnly.Add(new ProjectSuggestion(v.Name, GroupVsCode, $"{Describe(targets, null)}  ·  {v.Path}", targets));
        }

        return matched.Concat(unityOnly).Concat(vsOnly).ToList();
    }

    static string Describe(List<LaunchTarget> targets, string? unityVersion)
        => string.Join(" + ", targets.Select(t =>
            t.Kind == TargetKind.Unity && !string.IsNullOrEmpty(unityVersion) ? $"Unity {unityVersion}" : KindInfo.Name(t.Kind)));

    public static string Norm(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return "";
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }
}
