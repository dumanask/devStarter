using System.Text.Json.Serialization;
using System.Windows.Media;
using DevStarter.Discovery;
using DevStarter.Services;

namespace DevStarter.Models;

public enum TargetKind { Unity, VsCode, VisualStudio, Chrome, Blender, Docker, Folder, Terminal, Url, Custom }

// Bir projeye bağlı tek bir "açılacak şey": Unity projesi, VS Code klasörü, Chrome sayfaları...
public class LaunchTarget : ObservableObject
{
    TargetKind kind;
    string label = "";
    string path = "";
    List<string> urls = new();
    string chromeProfile = "";
    string exeOverride = "";
    string args = "";
    string workingDir = "";
    bool enabled = true;
    int delayMs;
    string? error;

    public TargetKind Kind { get => kind; set { if (Set(ref kind, value)) RaiseDisplay(); } }

    // Kullanıcının verdiği görünen ad (boşsa otomatik üretilir)
    public string Label { get => label; set { if (Set(ref label, value ?? "")) RaiseDisplay(); } }

    // Klasör, dosya veya URL (türe göre)
    public string Path { get => path; set { if (Set(ref path, value ?? "")) RaiseDisplay(); } }

    // Chrome: açılacak sayfalar
    public List<string> Urls
    {
        get => urls;
        set
        {
            urls = value ?? new();
            Raise();
            Raise(nameof(UrlsText));
            RaiseDisplay();
        }
    }

    // Chrome: profil klasör adı (ör. "Profile 1")
    public string ChromeProfile { get => chromeProfile; set { if (Set(ref chromeProfile, value ?? "")) RaiseDisplay(); } }

    // Boşsa program yolu otomatik bulunur
    public string ExeOverride { get => exeOverride; set => Set(ref exeOverride, value ?? ""); }

    public string Args { get => args; set => Set(ref args, value ?? ""); }
    public string WorkingDir { get => workingDir; set => Set(ref workingDir, value ?? ""); }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public int DelayMs { get => delayMs; set => Set(ref delayMs, Math.Max(0, value)); }

    [JsonIgnore]
    public string UrlsText
    {
        get => string.Join(Environment.NewLine, urls);
        set => Urls = (value ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    // Son açma denemesinin hatası (kaydedilmez)
    [JsonIgnore]
    public string? Error
    {
        get => error;
        set
        {
            if (Set(ref error, value))
            {
                Raise(nameof(HasError));
                Raise(nameof(Summary));
            }
        }
    }

    [JsonIgnore] public bool HasError => error != null;
    [JsonIgnore] public string KindName => KindInfo.Name(kind);
    [JsonIgnore] public string Glyph => KindInfo.Glyph(kind);
    [JsonIgnore] public ImageSource? Icon => KindInfo.IconFor(this);

    [JsonIgnore]
    public string Detail => kind switch
    {
        TargetKind.Chrome => urls.Count == 0
            ? ChromeProfiles.NameOf(chromeProfile)
            : HostOf(urls[0]) + (urls.Count > 1 ? $" +{urls.Count - 1}" : ""),
        TargetKind.Docker => path.Length == 0 ? "Desktop" : DockerName(path),
        TargetKind.Url => KindInfo.IsWebUrl(path) ? HostOf(path) : LastSegment(path),
        _ => LastSegment(path)
    };

    [JsonIgnore]
    public string DisplayName => label.Length > 0
        ? label
        : Detail.Length > 0 ? $"{KindName} — {Detail}" : KindName;

    [JsonIgnore]
    public string Location => kind == TargetKind.Chrome
        ? (urls.Count > 0 ? string.Join("\n", urls) : "Yeni pencere")
        : path;

    [JsonIgnore]
    public string Summary => DisplayName
        + (Location.Length > 0 ? "\n" + Location : "")
        + (enabled ? "" : "\n(devre dışı)")
        + (error != null ? "\n⚠ " + error : "");

    void RaiseDisplay()
    {
        Raise(nameof(KindName));
        Raise(nameof(Glyph));
        Raise(nameof(Detail));
        Raise(nameof(DisplayName));
        Raise(nameof(Location));
        Raise(nameof(Summary));
        Raise(nameof(Icon));
    }

    public LaunchTarget Clone() => new()
    {
        Kind = kind,
        Label = label,
        Path = path,
        Urls = new List<string>(urls),
        ChromeProfile = chromeProfile,
        ExeOverride = exeOverride,
        Args = args,
        WorkingDir = workingDir,
        Enabled = enabled,
        DelayMs = delayMs
    };

    static string LastSegment(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        if (p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return "";
        var trimmed = p.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    static string DockerName(string p)
    {
        // compose dosyası ise içinde bulunduğu klasörün adı daha anlamlı
        if (System.IO.Path.HasExtension(p))
        {
            var dir = System.IO.Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) return LastSegment(dir);
        }
        return LastSegment(p);
    }

    static string HostOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0)
            return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        if (Uri.TryCreate("https://" + url, UriKind.Absolute, out var u2) && u2.Host.Length > 0)
            return u2.Host;
        return url;
    }
}
