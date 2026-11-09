using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DevStarter.Services;

// Zaten açık olan bir programın penceresini bulup öne getirir
public static class WindowFocus
{
    public static bool Focus(string processName, Func<string, bool> titleMatch)
    {
        var procs = Process.GetProcessesByName(processName);
        HashSet<uint> pids;
        try { pids = procs.Select(p => (uint)p.Id).ToHashSet(); }
        finally { foreach (var p in procs) p.Dispose(); }
        if (pids.Count == 0) return false;

        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            var title = GetTitle(h);
            if (title.Length > 0 && titleMatch(title))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) return false;
        Activate(found);
        return true;
    }

    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (SetForegroundWindow(hwnd)) return;

        // Ön plan kilidine takılırsa giriş kuyruklarını geçici olarak bağla
        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint current = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != current) AttachThreadInput(current, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (fgThread != 0 && fgThread != current) AttachThreadInput(current, fgThread, false);
        }
    }

    static string GetTitle(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);

    const int SW_RESTORE = 9;
}
