using System.IO;
using DevStarter.Models;
using DevStarter.Services;

namespace DevStarter.Launchers;

public interface ILauncher
{
    // Başarılıysa isteğe bağlı bir bilgi notu döner ("zaten açıktı" gibi), başarısızsa LaunchException fırlatır
    Task<string?> LaunchAsync(LaunchTarget target);
}

public class LaunchException(string message) : Exception(message);

public static class LauncherRegistry
{
    static readonly Dictionary<TargetKind, ILauncher> map = new()
    {
        [TargetKind.Unity] = new UnityLauncher(),
        [TargetKind.VsCode] = new VsCodeLauncher(),
        [TargetKind.VisualStudio] = new VisualStudioLauncher(),
        [TargetKind.Chrome] = new ChromeLauncher(),
        [TargetKind.Blender] = new BlenderLauncher(),
        [TargetKind.Docker] = new DockerLauncher(),
        [TargetKind.Folder] = new ShellLauncher(),
        [TargetKind.Url] = new ShellLauncher(),
        [TargetKind.Terminal] = new TerminalLauncher(),
        [TargetKind.Custom] = new CustomLauncher()
    };

    public static ILauncher Get(TargetKind kind) => map[kind];
}

static class LaunchHelpers
{
    public static string RequireDir(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new LaunchException($"{what} yolu boş");
        if (!Directory.Exists(path)) throw new LaunchException($"Klasör bulunamadı: {path}");
        return path;
    }

    // Hedefe özel program yolu varsa onu, yoksa otomatik bulunanı kullanır
    public static string Exe(LaunchTarget t, string tool, string displayName)
    {
        if (t.ExeOverride.Length > 0)
        {
            if (!File.Exists(t.ExeOverride)) throw new LaunchException($"Program bulunamadı: {t.ExeOverride}");
            return t.ExeOverride;
        }
        return ToolLocator.Find(tool)
            ?? throw new LaunchException($"{displayName} bulunamadı — Düzenle ekranından program yolunu seç");
    }
}
