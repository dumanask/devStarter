using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DevStarter.Services;

// Proje simgeleri: seçilen resim %APPDATA%\devStarter\icons altına PNG olarak kopyalanır,
// böylece orijinal dosya taşınsa/silinse de simge kaybolmaz.
public static class ProjectIcons
{
    public static readonly string Dir = Path.Combine(Log.Dir, "icons");

    public const string FileFilter =
        "Resimler (*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif;*.webp;*.exe)|*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif;*.webp;*.exe|Tüm dosyalar (*.*)|*.*";

    static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".ico", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsImageFile(string path)
        => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // Resim/ikon/exe dosyasından simge oluşturur; kaydedilen PNG'nin yolunu döner
    public static string? Import(string sourcePath)
    {
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            BitmapSource? bmp = ext switch
            {
                ".exe" or ".lnk" or ".dll" => IconService.GetIcon(sourcePath) as BitmapSource,
                ".ico" => LargestIconFrame(sourcePath),
                _ => LoadBitmap(sourcePath, 256)
            };
            return bmp == null ? null : Save(bmp);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"icon import {sourcePath}");
            return null;
        }
    }

    // Panodan veya başka bir kaynaktan gelen görüntüyü kaydeder
    public static string? Save(BitmapSource bmp)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var scaled = Downscale(bmp, 256);
            var file = Path.Combine(Dir, $"{Guid.NewGuid():N}.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(scaled));
            using (var fs = File.Create(file)) enc.Save(fs);
            return file;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "icon save");
            return null;
        }
    }

    public static ImageSource? Load(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (cache.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        try { if (File.Exists(path)) img = LoadBitmap(path, 128); }
        catch (Exception ex) { Log.Error(ex, $"icon load {path}"); }
        cache[path] = img;
        return img;
    }

    // Sadece bizim klasörümüzdeki dosyaları siler (kullanıcının kendi resimlerine dokunmaz)
    public static void DeleteIfOwned(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(Path.GetFullPath(Dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                cache.Remove(path);
                File.Delete(full);
            }
        }
        catch { }
    }

    static BitmapSource LoadBitmap(string path, int maxSize)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        img.UriSource = new Uri(path);
        img.EndInit();
        img.Freeze();
        return Downscale(img, maxSize);
    }

    static BitmapSource? LargestIconFrame(string path)
    {
        using var fs = File.OpenRead(path);
        var decoder = new IconBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
        frame?.Freeze();
        return frame;
    }

    static BitmapSource Downscale(BitmapSource src, int maxSize)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        if (w <= maxSize && h <= maxSize) return src;
        double scale = (double)maxSize / Math.Max(w, h);
        var scaled = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }
}
