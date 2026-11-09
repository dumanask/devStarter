using System.Diagnostics;
using System.IO;
using DevStarter.Models;
using DevStarter.Services;

namespace DevStarter.Launchers;

// Docker Desktop kapalıysa başlatır, motor hazır olunca isteğe bağlı compose/komut çalıştırır
public class DockerLauncher : ILauncher
{
    static readonly TimeSpan EngineTimeout = TimeSpan.FromMinutes(2);

    public async Task<string?> LaunchAsync(LaunchTarget t)
    {
        var cli = ToolLocator.Find(ToolLocator.Docker)
            ?? throw new LaunchException("docker komutu bulunamadı — Docker Desktop yüklü mü?");

        string? note = null;
        if (!await EngineReady(cli))
        {
            if (!ProcessService.IsRunning("Docker Desktop"))
            {
                var desktop = ToolLocator.Find(ToolLocator.DockerDesktop)
                    ?? throw new LaunchException("Docker Desktop bulunamadı");
                ProcessService.Start(desktop);
            }
            var sw = Stopwatch.StartNew();
            while (!await EngineReady(cli))
            {
                if (sw.Elapsed > EngineTimeout)
                    throw new LaunchException("Docker motoru 2 dakika içinde hazır olmadı");
                await Task.Delay(2000);
            }
            note = "Docker Desktop başlatıldı";
        }

        if (t.Path.Length == 0 && t.Args.Length == 0)
            return note ?? "Docker zaten çalışıyor";

        string workDir;
        string args;
        if (t.Path.Length > 0 && File.Exists(t.Path))
        {
            workDir = Path.GetDirectoryName(t.Path)!;
            args = t.Args.Length > 0 ? t.Args : "compose -f " + ProcessService.Quote(t.Path) + " up -d";
        }
        else if (t.Path.Length > 0 && Directory.Exists(t.Path))
        {
            workDir = t.Path;
            args = t.Args.Length > 0 ? t.Args : "compose up -d";
        }
        else if (t.Path.Length > 0)
        {
            throw new LaunchException($"Bulunamadı: {t.Path}");
        }
        else
        {
            workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            args = t.Args;
        }

        var r = await ProcessService.RunAsync(cli, args, workDir, TimeSpan.FromMinutes(5));
        if (r.ExitCode != 0)
            throw new LaunchException($"docker {args} başarısız: {LastLine(r.Error.Length > 0 ? r.Error : r.Output)}");

        var done = args.StartsWith("compose", StringComparison.OrdinalIgnoreCase) ? "compose up tamamlandı" : "komut tamamlandı";
        return note != null ? $"{note}, {done}" : done;
    }

    static async Task<bool> EngineReady(string cli)
    {
        try
        {
            var r = await ProcessService.RunAsync(cli, "info --format \"{{.ServerVersion}}\"", null, TimeSpan.FromSeconds(15));
            return r.ExitCode == 0 && r.Output.Trim().Length > 0;
        }
        catch { return false; }
    }

    static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : "bilinmeyen hata";
    }
}
