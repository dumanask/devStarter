using System.Collections.Concurrent;
using System.IO;
using DevStarter.Models;
using DevStarter.Services;

namespace DevStarter.Launchers;

public class UnityLauncher : ILauncher
{
    // Yeni başlatılan projeler: Unity kilit dosyasını oluşturana kadar ikinci kez başlatmayı engeller
    static readonly ConcurrentDictionary<string, DateTime> recent = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);

    public Task<string?> LaunchAsync(LaunchTarget t)
    {
        var dir = Path.TrimEndingDirectorySeparator(LaunchHelpers.RequireDir(t.Path, "Unity projesi"));
        var name = Path.GetFileName(dir);

        if (IsOpen(dir))
        {
            // Unity pencere başlığı proje adıyla başlar: "OyunProjem - SampleScene - Windows, Mac, Linux - Unity 6.6 ..."
            bool focused = WindowFocus.Focus("Unity",
                title => title.StartsWith(name + " - ", StringComparison.OrdinalIgnoreCase)
                      || title.Equals(name, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult<string?>(focused ? "zaten açıktı, öne getirildi" : "zaten açık");
        }

        if (recent.TryGetValue(dir, out var at) && DateTime.UtcNow - at < StartupGrace)
            return Task.FromResult<string?>("zaten başlatılıyor");

        var version = ReadVersion(dir)
            ?? throw new LaunchException("ProjectSettings/ProjectVersion.txt yok — bu bir Unity projesi mi?");

        string? exe = t.ExeOverride.Length > 0 ? t.ExeOverride : ToolLocator.UnityEditor(version);
        if (exe == null || !File.Exists(exe))
        {
            var hub = ToolLocator.Find(ToolLocator.UnityHub);
            if (hub != null) ProcessService.Start(hub);
            throw new LaunchException($"Unity {version} yüklü değil" + (hub != null ? " — Unity Hub açıldı" : ""));
        }

        ProcessService.Start(exe, ProcessService.WithExtra(ProcessService.Join(new[] { "-projectPath", dir }), t.Args));
        recent[dir] = DateTime.UtcNow;
        return Task.FromResult<string?>(null);
    }

    // Unity açık bir projenin Temp\UnityLockfile dosyasını kilitli tutar
    public static bool IsOpen(string dir)
    {
        var lockFile = Path.Combine(dir, "Temp", "UnityLockfile");
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var fs = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    public static string? ReadVersion(string dir)
    {
        try
        {
            var file = Path.Combine(dir, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(file)) return null;
            foreach (var line in File.ReadLines(file))
                if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                    return line["m_EditorVersion:".Length..].Trim();
        }
        catch { }
        return null;
    }
}
