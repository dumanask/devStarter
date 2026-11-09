using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using DevStarter.Services;

namespace DevStarter.Discovery;

public record ChromeOpenPage(string ProfileDir, string ProfileName, string Title, string Url, int TabCount);

// Şu an açık Chrome pencerelerindeki aktif sayfalar (UI Automation ile adres çubuğundan okunur).
// Chrome çalışırken oturum dosyasını kilitlediği için tüm sekmelerin adresleri diskten okunamıyor;
// her pencerenin görünen sayfası okunabiliyor. Arka plan iş parçacığında çağrılmalı.
//
// Performans notları:
// - Masaüstü kökünden (RootElement) arama YAPILMAZ: o, devStarter'ın kendi WPF pencerelerini de sorgular;
//   WPF erişilebilirlik ağacını kurmaya başlar ve aynı süreç içi çağrılar zaman aşımına kadar bekleyebilir.
//   Chrome pencereleri Win32 ile bulunur, UIA yalnızca onlara uygulanır.
// - Web içeriğine (sayfa DOM'u) inen geniş aramalar yapılmaz; Chrome'un tüm sekmeler için erişilebilirlik
//   ağacı kurmasına ve sistemi yormasına yol açar.
public static class ChromeWindows
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static List<ChromeOpenPage> Load()
    {
        try
        {
            var task = Task.Run(Scan);
            return task.Wait(Timeout) ? task.Result : new List<ChromeOpenPage>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Chrome windows (UIA)");
            return new List<ChromeOpenPage>();
        }
    }

    static List<ChromeOpenPage> Scan()
    {
        var result = new List<ChromeOpenPage>();
        var profiles = ChromeProfiles.Load();
        var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        var avatarCond = new PropertyCondition(AutomationElement.ClassNameProperty, "AvatarToolbarButton");
        var tabStripCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab);
        var tabItemCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

        foreach (var (hwnd, title) in ChromeTopWindows())
        {
            try
            {
                var w = AutomationElement.FromHandle(hwnd);

                // Adres çubuğu, araç çubuğunda web içeriğinden önce gelir; FindFirst ilk eşleşmede durur
                var edit = w.FindFirst(TreeScope.Descendants, editCond);
                if (edit == null || !edit.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) continue;
                var url = ((ValuePattern)vp).Current.Value?.Trim() ?? "";
                if (url.Length == 0) continue;
                if (!url.Contains("://") && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;

                // Profil: araç çubuğundaki avatar düğmesinin adı profil adıdır
                var avatarName = w.FindFirst(TreeScope.Descendants, avatarCond)?.Current.Name ?? "";
                var profile = profiles
                    .Where(p => p.Name.Length > 0 && avatarName.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.Name.Length)
                    .FirstOrDefault();

                // Sekme sayısı yalnızca sekme şeridinden (web içeriğine inmeden)
                int tabs = 0;
                var strip = w.FindFirst(TreeScope.Descendants, tabStripCond);
                if (strip != null) tabs = strip.FindAll(TreeScope.Children, tabItemCond).Count;

                result.Add(new ChromeOpenPage(profile?.Dir ?? "", profile?.Name ?? "", StripSuffix(title), url, tabs));
            }
            catch { }
        }
        return result;
    }

    static List<(IntPtr Hwnd, string Title)> ChromeTopWindows()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("chrome"))
        {
            pids.Add((uint)p.Id);
            p.Dispose();
        }
        var list = new List<(IntPtr, string)>();
        if (pids.Count == 0) return list;

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() != "Chrome_WidgetWin_1") return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            list.Add((h, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // "Sayfa başlığı - Google Chrome" → "Sayfa başlığı"
    static string StripSuffix(string title)
    {
        int i = title.LastIndexOf(" - Google Chrome", StringComparison.Ordinal);
        return i > 0 ? title[..i] : title;
    }

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
}
