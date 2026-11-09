using System.IO;
using System.Windows.Media;
using DevStarter.Models;

namespace DevStarter.Services;

// Hedef türlerinin görünen adları ve ikonları
public static class KindInfo
{
    const string TerminalAumid = @"shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";

    static readonly Dictionary<TargetKind, ImageSource?> kindIcons = new();

    public static string Name(TargetKind k) => k switch
    {
        TargetKind.Unity => "Unity",
        TargetKind.VsCode => "VS Code",
        TargetKind.VisualStudio => "Visual Studio",
        TargetKind.Chrome => "Chrome",
        TargetKind.Blender => "Blender",
        TargetKind.Docker => "Docker",
        TargetKind.Folder => "Klasör",
        TargetKind.Terminal => "Terminal",
        TargetKind.Url => "Web / Dosya",
        TargetKind.Custom => "Uygulama",
        _ => k.ToString()
    };

    // İkon bulunamazsa gösterilecek Segoe Fluent Icons / MDL2 karakteri
    public static string Glyph(TargetKind k) => k switch
    {
        TargetKind.Folder => "",
        TargetKind.Url => "",
        TargetKind.Terminal => "",
        TargetKind.Docker => "",
        TargetKind.Custom => "",
        _ => ""
    };

    // UI iş parçacığından çağrılmalı (IconService önbelleği thread-safe değil)
    public static ImageSource? KindIcon(TargetKind k)
    {
        if (kindIcons.TryGetValue(k, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            if (k == TargetKind.Terminal)
            {
                if (ToolLocator.Find(ToolLocator.Terminal) != null) icon = IconService.GetIcon(TerminalAumid);
                icon ??= Get(ToolLocator.Find(ToolLocator.PowerShell));
            }
            else
            {
                icon = Get(k switch
                {
                    TargetKind.Unity => ToolLocator.Find(ToolLocator.UnityHub) ?? ToolLocator.AnyUnityEditor(),
                    TargetKind.VsCode => ToolLocator.Find(ToolLocator.Code),
                    TargetKind.VisualStudio => ToolLocator.Find(ToolLocator.VisualStudio),
                    TargetKind.Chrome => ToolLocator.Find(ToolLocator.Chrome),
                    TargetKind.Blender => ToolLocator.Find(ToolLocator.Blender),
                    TargetKind.Docker => ToolLocator.Find(ToolLocator.DockerDesktop),
                    TargetKind.Folder => ToolLocator.Find(ToolLocator.Explorer),
                    TargetKind.Url => ToolLocator.DefaultBrowser(),
                    _ => null
                });
            }
        }
        catch (Exception ex) { Log.Error(ex, $"kind icon {k}"); }
        // Başarısız sonucu kalıcı saklama: program yüklüyse bir sonraki çizimde tekrar denensin.
        // (Program hiç yoksa ToolLocator hızlıca null döner, maliyeti yok.)
        if (icon != null) kindIcons[k] = icon;
        return icon;
    }

    public static ImageSource? IconFor(LaunchTarget t)
    {
        try
        {
            if (t.Kind == TargetKind.Custom && t.Path.Length > 0)
                return IconService.GetIcon(t.Path) ?? KindIcon(TargetKind.Custom);
            if (t.Kind == TargetKind.Url && t.Path.Length > 0 && !IsWebUrl(t.Path) && (File.Exists(t.Path) || Directory.Exists(t.Path)))
                return IconService.GetIcon(t.Path) ?? KindIcon(TargetKind.Url);
        }
        catch { }
        return KindIcon(t.Kind);
    }

    public static bool IsWebUrl(string s)
        => Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    static ImageSource? Get(string? path) => string.IsNullOrEmpty(path) ? null : IconService.GetIcon(path);
}
