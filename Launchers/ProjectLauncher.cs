using DevStarter.Models;
using DevStarter.Services;

namespace DevStarter.Launchers;

public record LaunchResult(LaunchTarget Target, bool Ok, string? Message);

public static class ProjectLauncher
{
    // Hedefleri paralel başlatır (her biri kendi gecikmesiyle). Bir hedefin hatası diğerlerini durdurmaz.
    // Proje zaten açılıyorsa null döner.
    public static async Task<List<LaunchResult>?> LaunchAsync(Project project, IEnumerable<LaunchTarget>? only = null)
    {
        if (project.IsLaunching) return null;

        var targets = (only ?? project.Targets.Where(t => t.Enabled)).ToList();
        if (targets.Count == 0) return new List<LaunchResult>();

        project.IsLaunching = true;
        IEnumerable<LaunchTarget> toClear = only == null ? project.Targets : targets;
        foreach (var t in toClear) t.Error = null;
        project.RaiseStatus();
        try
        {
            var results = await Task.WhenAll(targets.Select(RunOne));
            return results.ToList();
        }
        finally
        {
            project.IsLaunching = false;
            project.RaiseStatus();
        }
    }

    // UI iş parçacığından çağrılmalı: sonuç (Error) UI tarafında atanır
    public static async Task<LaunchResult> RunOne(LaunchTarget t)
    {
        try
        {
            if (t.DelayMs > 0) await Task.Delay(t.DelayMs);
            var launcher = LauncherRegistry.Get(t.Kind);
            var note = await Task.Run(() => launcher.LaunchAsync(t));
            t.Error = null;
            return new LaunchResult(t, true, note);
        }
        catch (Exception ex)
        {
            if (ex is not LaunchException) Log.Error(ex, $"launch {t.Kind} {t.Path}");
            t.Error = ex.Message;
            return new LaunchResult(t, false, ex.Message);
        }
    }
}
