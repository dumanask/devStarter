using System.IO;
using System.Runtime.InteropServices;

namespace DevStarter.Services;

public class InstalledApp
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public static class AppCatalog
{
    static List<InstalledApp>? cache;
    static DateTime cacheTime;
    static readonly TimeSpan ttl = TimeSpan.FromMinutes(5);

    public static Task<List<InstalledApp>> GetAppsAsync()
    {
        if (cache != null && DateTime.UtcNow - cacheTime < ttl)
            return Task.FromResult(cache);
        return Task.Run(() =>
        {
            cache = LoadFromAppsFolder();
            cacheTime = DateTime.UtcNow;
            return cache;
        });
    }

    // Başlat menüsünün kullandığı "Uygulamalar" klasörünü (shell:AppsFolder) tarar;
    // hem masaüstü kısayollarını hem mağaza (UWP) uygulamalarını içerir.
    static List<InstalledApp> LoadFromAppsFolder()
    {
        var result = new List<InstalledApp>();
        try
        {
            var desktop = (IShellFolder)new ShellDesktop();
            uint eaten;
            uint attrs = 0;
            if (desktop.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, "shell:AppsFolder", out eaten, out IntPtr appsPidl, ref attrs) != 0 || appsPidl == IntPtr.Zero)
                return result;
            try
            {
                var iidFolder = new Guid(IIDIShellFolder);
                if (desktop.BindToObject(appsPidl, IntPtr.Zero, ref iidFolder, out IntPtr folderPtr) != 0 || folderPtr == IntPtr.Zero)
                    return result;
                try
                {
                    var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
                    if (folder.EnumObjects(IntPtr.Zero, SHCONTF_FOLDERS, out IEnumIDList? enumIds) != 0 || enumIds == null)
                        return result;

                    var iidItem = new Guid(IIDIShellItem);
                    for (;;)
                    {
                        if (enumIds.Next(1, out IntPtr child, out uint fetched) != 0 || fetched != 1) break;
                        IntPtr full = ILCombine(appsPidl, child);
                        try
                        {
                            if (SHCreateItemFromIDList(full, ref iidItem, out IntPtr itemPtr) == 0 && itemPtr != IntPtr.Zero)
                            {
                                try
                                {
                                    var item = (IShellItem)Marshal.GetObjectForIUnknown(itemPtr);
                                    string name = GetItemName(item, SIGDN_NORMAL);
                                    string parse = GetItemName(item, SIGDN_DESKTOPABSOLUTEPARSING);

                                    // AUMID formatındakileri (ör. "Microsoft.X_...!App") shell: önekiyle standardize et
                                    string path = parse;
                                    if (!parse.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                                        && !parse.StartsWith(@"\\", StringComparison.Ordinal)
                                        && parse.Contains('!')
                                        && !parse.Contains('\\', StringComparison.Ordinal))
                                    {
                                        path = "shell:AppsFolder\\" + parse;
                                    }

                                    if (name.Length > 0 && path.Length > 0)
                                        result.Add(new InstalledApp { Name = name, Path = path });
                                }
                                finally { Marshal.Release(itemPtr); }
                            }
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(full);
                            Marshal.FreeCoTaskMem(child);
                        }
                    }
                }
                finally { Marshal.Release(folderPtr); }
            }
            finally { Marshal.FreeCoTaskMem(appsPidl); }
        }
        catch { }

        if (result.Count == 0)
            result = LoadFromStartMenu();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = result.Where(a => seen.Add(a.Path)).ToList();
        deduped.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return deduped;
    }

    static string GetItemName(IShellItem item, uint flags)
    {
        try
        {
            if (item.GetDisplayName(flags, out IntPtr ptr) == 0 && ptr != IntPtr.Zero)
            {
                string s = Marshal.PtrToStringUni(ptr) ?? "";
                Marshal.FreeCoTaskMem(ptr);
                return s;
            }
        }
        catch { }
        return "";
    }

    // Yedek: Başlat menüsü klasörlerinden .lnk taraması
    static List<InstalledApp> LoadFromStartMenu()
    {
        var result = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
        })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (name.Length == 0 || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.Add(lnk))
                        result.Add(new InstalledApp { Name = name, Path = lnk });
                }
            }
            catch { }
        }
        return result;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046")]
    class ShellDesktop { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IEnumIDList ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr[] apidl, ref Guid riid, ref uint rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr lpName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumIDList
    {
        [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumIDList ppenum);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll")]
    static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll")]
    static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid, out IntPtr ppv);

    const string IIDIShellFolder = "000214E6-0000-0000-C000-000000000046";
    const string IIDIShellItem = "43826d1e-e718-42ee-bc55-a1e261c37bfe";
    const uint SIGDN_NORMAL = 0x00000000;
    const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    const uint SHCONTF_FOLDERS = 0x20;
}
