using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DevStarter.Services;

public static class IconService
{
    // Simgeler donmuş (Freeze) BitmapSource olduğundan iş parçacıkları arası güvenli; önbellek de eşzamanlı
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetCached(string path, out ImageSource? icon)
    {
        var found = cache.TryGetValue(path, out var c);
        icon = c;
        return found;
    }

    // Kabuk simge listesi (SHGetImageList/IImageList) ve GDI dönüşümü aynı anda iki iş parçacığından
    // çağrılınca simge boş dönebiliyor; çıkarma işlemi tek seferde bir iş parçacığıyla sınırlandı.
    static readonly object gate = new();

    public static ImageSource? GetIcon(string path)
    {
        if (cache.TryGetValue(path, out var cached)) return cached;
        lock (gate)
        {
            if (cache.TryGetValue(path, out cached)) return cached;
            var icon = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                ? FromShellItem(path)
                : FromHIcon(GetLargeIconHandle(path));
            if (icon == null) return null;
            cache[path] = icon;
            return icon;
        }
    }

    static BitmapSource? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(hIcon);
            using var bitmap = icon.ToBitmap();
            var hBitmap = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(hBitmap); }
        }
        finally { DestroyIcon(hIcon); }
    }

    // shell: parsing adları için (ör. UWP uygulamaları) kabuk öğesinden simge alır
    static BitmapSource? FromShellItem(string parsingName)
    {
        try
        {
            var iid = new Guid(IIDIShellItemImageFactory);
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out IntPtr factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
                return null;
            // GetUniqueObjectForIUnknown: sarmalayıcı (RCW) bu iş parçacığında oluşturulur. Paylaşılan RCW,
            // başka bir iş parçacığında (ör. arka plan simge yükleyicisi) oluşturulduysa çağrılar başarısız oluyordu.
            object? rcw = null;
            try
            {
                rcw = Marshal.GetUniqueObjectForIUnknown(factoryPtr);
                var factory = (IShellItemImageFactory)rcw;
                if (factory.GetImage(new SIZE { cx = 48, cy = 48 }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out IntPtr hBitmap) != 0 || hBitmap == IntPtr.Zero)
                    return null;
                try { return FromHBitmap(hBitmap); }
                finally { DeleteObject(hBitmap); }
            }
            finally
            {
                if (rcw != null) Marshal.FinalReleaseComObject(rcw);
                Marshal.Release(factoryPtr);
            }
        }
        catch { return null; }
    }

    static BitmapSource? FromHBitmap(IntPtr hBitmap)
    {
        if (GetObject(hBitmap, Marshal.SizeOf(typeof(BITMAP)), out BITMAP bm) == 0) return null;
        int w = bm.bmWidth;
        int h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0) return null;

        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;

        var pixels = new byte[w * h * 4];
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hBitmap, 0, (uint)h, pixels, ref bmi, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }

        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha)
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        source.Freeze();
        return source;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c0c9d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    // Kabuktan 48x48 simge alır; .lnk kısayollarının hedef simgesini de çözer
    static IntPtr GetLargeIconHandle(string path)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_SYSICONINDEX) != IntPtr.Zero)
        {
            var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out IntPtr listPtr) == 0 && listPtr != IntPtr.Zero)
            {
                // Sistem simge listesi süreç genelinde tektir; paylaşılan RCW başka iş parçacığında
                // oluşturulmuşsa çağrı hata veriyordu. Her çağrıda bu iş parçacığına ait ayrı RCW kullan.
                object? rcw = null;
                try
                {
                    rcw = Marshal.GetUniqueObjectForIUnknown(listPtr);
                    var list = (IImageList)rcw;
                    if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, out IntPtr hIcon) == 0 && hIcon != IntPtr.Zero)
                        return hIcon;
                }
                catch { /* aşağıdaki SHGetFileInfo yedeğine düş */ }
                finally
                {
                    if (rcw != null) Marshal.FinalReleaseComObject(rcw);
                    Marshal.Release(listPtr);
                }
            }
        }

        var fallback = new SHFILEINFO();
        SHGetFileInfo(path, 0, ref fallback, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON);
        return fallback.hIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Overlay(int iImage, int iOverlay, ref int pi);
        [PreserveSig] int AddMasked(IntPtr hbmMask, uint crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, uint flags, out IntPtr hicon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    static extern int GetObject(IntPtr hgdiobj, int cb, out BITMAP lpObject);

    [DllImport("gdi32.dll")]
    static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("shell32.dll")]
    static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    const string IIDIShellItemImageFactory = "bcc18b79-ba16-442f-80c4-8a59c30c0c9d";
    const int SIIGBF_BIGGERSIZEOK = 0x1;
    const int SIIGBF_ICONONLY = 0x4;

    const uint SHGFI_ICON = 0x100;
    const uint SHGFI_LARGEICON = 0x0;
    const uint SHGFI_SYSICONINDEX = 0x4000;
    const int SHIL_EXTRALARGE = 0x2;
    const int ILD_TRANSPARENT = 0x1;
}
