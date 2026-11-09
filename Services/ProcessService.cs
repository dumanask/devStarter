using System.Diagnostics;
using System.IO;
using System.Text;

namespace DevStarter.Services;

public record RunResult(int ExitCode, string Output, string Error);

public static class ProcessService
{
    // Görünür bir programı başlatır (kabuk üzerinden; devStarter kapansa da açık kalır)
    public static void Start(string file, string args = "", string? workingDir = null)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = true, Arguments = args };
        if (!string.IsNullOrEmpty(workingDir))
        {
            psi.WorkingDirectory = workingDir;
        }
        else if (!file.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(file))
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
        }
        using var _ = Process.Start(psi);
    }

    // Dosya, klasör veya URL'yi varsayılan uygulamayla açar
    public static void ShellOpen(string target)
    {
        using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    // Pencere göstermeden bir komut çalıştırır ve çıktısını toplar (docker vb.)
    public static async Task<RunResult> RunAsync(string file, string args, string? workingDir, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} başlatılamadı");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return new RunResult(-1, "", "zaman aşımı");
        }
        return new RunResult(p.ExitCode, await stdout, await stderr);
    }

    public static bool IsRunning(string processName)
    {
        var list = Process.GetProcessesByName(processName);
        try { return list.Length > 0; }
        finally { foreach (var p in list) p.Dispose(); }
    }

    // Windows komut satırı kurallarına göre tek bir argümanı tırnaklar
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    // Kullanıcının yazdığı ek argümanları sona ekler
    public static string WithExtra(string args, string extra)
        => string.IsNullOrWhiteSpace(extra) ? args : (args.Length > 0 ? args + " " : "") + extra.Trim();
}
