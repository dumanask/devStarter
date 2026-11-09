using System.IO;
using DevStarter.Models;
using DevStarter.Services;

namespace DevStarter.Launchers;

// VS Code zaten açık olan klasörün penceresini kendisi öne getirir
public class VsCodeLauncher : ILauncher
{
    public Task<string?> LaunchAsync(LaunchTarget t)
    {
        var exe = LaunchHelpers.Exe(t, ToolLocator.Code, "VS Code");
        if (t.Path.Length == 0)
        {
            ProcessService.Start(exe, t.Args);
            return Task.FromResult<string?>(null);
        }
        if (!Directory.Exists(t.Path) && !File.Exists(t.Path))
            throw new LaunchException($"Bulunamadı: {t.Path}");
        ProcessService.Start(exe, ProcessService.WithExtra(ProcessService.Quote(t.Path), t.Args));
        return Task.FromResult<string?>(null);
    }
}

public class VisualStudioLauncher : ILauncher
{
    public Task<string?> LaunchAsync(LaunchTarget t)
    {
        if (t.Path.Length > 0 && File.Exists(t.Path) && t.ExeOverride.Length == 0 && t.Args.Length == 0)
        {
            // .sln ilişkisi doğru Visual Studio sürümünü seçer
            ProcessService.ShellOpen(t.Path);
            return Task.FromResult<string?>(null);
        }
        var exe = LaunchHelpers.Exe(t, ToolLocator.VisualStudio, "Visual Studio");
        if (t.Path.Length > 0 && !File.Exists(t.Path) && !Directory.Exists(t.Path))
            throw new LaunchException($"Bulunamadı: {t.Path}");
        var args = t.Path.Length > 0 ? ProcessService.Quote(t.Path) : "";
        ProcessService.Start(exe, ProcessService.WithExtra(args, t.Args));
        return Task.FromResult<string?>(null);
    }
}

public class ChromeLauncher : ILauncher
{
    public Task<string?> LaunchAsync(LaunchTarget t)
    {
        var exe = LaunchHelpers.Exe(t, ToolLocator.Chrome, "Chrome");
        var parts = new List<string>();
        if (t.ChromeProfile.Length > 0) parts.Add("--profile-directory=" + t.ChromeProfile);
        parts.Add("--new-window");
        parts.AddRange(t.Urls.Select(NormalizeUrl));
        ProcessService.Start(exe, ProcessService.WithExtra(ProcessService.Join(parts), t.Args));
        return Task.FromResult<string?>(null);
    }

    static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.Contains("://") || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("chrome:", StringComparison.OrdinalIgnoreCase) || File.Exists(url))
            return url;
        return "https://" + url;
    }
}

public class BlenderLauncher : ILauncher
{
    public Task<string?> LaunchAsync(LaunchTarget t)
    {
        if (t.Path.Length > 0 && !File.Exists(t.Path))
            throw new LaunchException($"Dosya bulunamadı: {t.Path}");

        string? exe = t.ExeOverride.Length > 0 ? t.ExeOverride : ToolLocator.Find(ToolLocator.Blender);
        if (exe == null || !File.Exists(exe))
        {
            if (t.Path.Length > 0)
            {
                // Blender bulunamazsa .blend dosyasını ilişkili uygulamayla aç
                ProcessService.ShellOpen(t.Path);
                return Task.FromResult<string?>(null);
            }
            throw new LaunchException("Blender bulunamadı — Düzenle ekranından program yolunu seç");
        }
        var args = t.Path.Length > 0 ? ProcessService.Quote(t.Path) : "";
        ProcessService.Start(exe, ProcessService.WithExtra(args, t.Args));
        return Task.FromResult<string?>(null);
    }
}

// Klasör (Gezgin) ve Web adresi / dosya
public class ShellLauncher : ILauncher
{
    public Task<string?> LaunchAsync(LaunchTarget t)
    {
        if (t.Kind == TargetKind.Folder)
        {
            var dir = LaunchHelpers.RequireDir(t.Path, "Klasör");
            ProcessService.Start(ToolLocator.Find(ToolLocator.Explorer) ?? "explorer.exe", ProcessService.Quote(dir));
            return Task.FromResult<string?>(null);
        }

        var target = t.Path.Trim();
        if (target.Length == 0 || target == "https://") throw new LaunchException("Adres boş");
        if (KindInfo.IsWebUrl(target) || File.Exists(target) || Directory.Exists(target) || target.Contains("://"))
            ProcessService.ShellOpen(target);
        else if (target.Contains('.') && !target.Contains('\\'))
            ProcessService.ShellOpen("https://" + target);
        else
            throw new LaunchException($"Bulunamadı: {target}");
        return Task.FromResult<string?>(null);
    }
}

public class TerminalLauncher : ILauncher
{
    public Task<string?> LaunchAsync(LaunchTarget t)
    {
        var dir = t.Path.Length > 0
            ? LaunchHelpers.RequireDir(t.Path, "Terminal klasörü")
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shell = ToolLocator.Find(ToolLocator.PowerShell) ?? "powershell.exe";
        var wt = ToolLocator.Find(ToolLocator.Terminal);

        if (wt != null)
        {
            var args = "-d " + ProcessService.Quote(dir);
            // wt komut satırında ';' yeni sekme ayıracıdır, kaçırılması gerekir
            if (t.Args.Length > 0)
                args += " " + ProcessService.Quote(shell) + " -NoExit -Command " + ProcessService.Quote(t.Args.Replace(";", "\\;"));
            ProcessService.Start(wt, args, dir);
        }
        else
        {
            var args = t.Args.Length > 0 ? "-NoExit -Command " + ProcessService.Quote(t.Args) : "-NoExit";
            ProcessService.Start(shell, args, dir);
        }
        return Task.FromResult<string?>(null);
    }
}

public class CustomLauncher : ILauncher
{
    public Task<string?> LaunchAsync(LaunchTarget t)
    {
        if (t.Path.Length == 0) throw new LaunchException("Program yolu boş");
        bool shell = t.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        if (!shell && !File.Exists(t.Path) && !Directory.Exists(t.Path))
            throw new LaunchException($"Bulunamadı: {t.Path}");
        ProcessService.Start(t.Path, t.Args, t.WorkingDir.Length > 0 ? t.WorkingDir : null);
        return Task.FromResult<string?>(null);
    }
}
