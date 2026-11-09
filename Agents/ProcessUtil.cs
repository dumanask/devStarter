using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DevStarter.Services;

namespace DevStarter.Agents;

static class ProcessUtil
{
    // procStart: Windows FILETIME (1601'den beri 100 ns). PID yeniden kullanılmışsa başlangıç zamanı tutmaz.
    public static bool IsAlive(int pid, string? procStart)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (long.TryParse(procStart, out var ft) && ft > 0)
            {
                try { return Math.Abs(p.StartTime.ToFileTimeUtc() - ft) < 20_000_000; }
                catch (Win32Exception) { return true; }
            }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch { return true; }
    }

    public static int ParentOf(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0
                ? (int)pbi.InheritedFromUniqueProcessId
                : 0;
        }
        finally { CloseHandle(h); }
    }

    // Oturumun penceresini öne getirir: VS Code içindeyse klasör adını taşıyan Code penceresi,
    // değilse süreç ağacında yukarı çıkarak bulunan ilk pencere (terminal)
    public static bool Focus(AgentSession s)
    {
        var folder = s.FolderName;
        bool vscode = s.Entrypoint.Contains("vscode", StringComparison.OrdinalIgnoreCase);
        if (vscode && folder.Length > 0 && WindowFocus.Focus("Code", t => t.Contains(folder, StringComparison.OrdinalIgnoreCase)))
            return true;

        int pid = s.Pid ?? 0;
        for (int depth = 0; pid > 0 && depth < 8; depth++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.MainWindowHandle != IntPtr.Zero && !string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                {
                    WindowFocus.Activate(p.MainWindowHandle);
                    return true;
                }
            }
            catch { return false; }
            pid = ParentOf(pid);
        }

        // Son çare: klasör adını taşıyan bir VS Code penceresi
        return folder.Length > 0 && WindowFocus.Focus("Code", t => t.Contains(folder, StringComparison.OrdinalIgnoreCase));
    }

    // Başka bir sürecin komut satırı (Windows 8.1+, ProcessCommandLineInformation = 60)
    public static string? CommandLine(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out int len);
            if (len <= 0 || len > 1 << 20) return null;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (NtQueryInformationProcess(h, 60, buf, len, out _) != 0) return null;
                var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
                return us.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buffer, int len, out int ret);

    public static bool AnyRunning(params string[] names)
    {
        foreach (var name in names)
        {
            var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
            bool any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (any) return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION pbi, int len, out int ret);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
}
