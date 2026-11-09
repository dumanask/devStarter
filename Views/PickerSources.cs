using System.IO;
using System.Windows;
using DevStarter.Discovery;
using DevStarter.Models;
using DevStarter.Services;
using Microsoft.Win32;

namespace DevStarter.Views;

// Seçici penceresinden gelen Chrome seçimi: tek sayfa veya bir grup (klasör / profil)
public record ChromePick(string ProfileDir, List<string> Urls, string Label, bool IsGroup)
{
    public LaunchTarget ToTarget() => new()
    {
        Kind = TargetKind.Chrome,
        ChromeProfile = ProfileDir,
        Urls = Urls.ToList(),
        Label = IsGroup ? Label : ""
    };
}

public static class PickerSources
{
    const string GNew = "Yeni";
    const string GVsCode = "VS Code — son klasörler";
    const string GChromeOpen = "Chrome — şu an açık sayfalar";
    const string GChromeFolders = "Chrome — yer imi klasörleri (hepsi tek pencerede)";
    const string GChromeBookmarks = "Chrome — yer imleri";
    const string GChromeProfiles = "Chrome — profil (boş pencere)";
    const string GBlender = "Blender";
    const string GOther = "Diğer";
    const string GApps = "Yüklü uygulamalar";

    // Sidebar'daki "+" : önerilen projeler (Unity Hub + VS Code eşleşmeleri) ve boş proje
    public static async Task<List<PickerItem>> ForProjects(
        Window owner, IEnumerable<Project> existing, Action<Project> addProject, Action newEmpty)
    {
        var usedPaths = existing.SelectMany(p => p.Targets).Select(t => t.Path).ToList();
        var suggestions = await Task.Run(() => Suggestions.Build(usedPaths));

        var items = new List<PickerItem>
        {
            new() { Group = GNew, Title = "Boş proje oluştur…", Subtitle = "Adını ve açılacakları kendin seç", Glyph = "", Run = newEmpty },
            new()
            {
                Group = GNew, Title = "Klasörden proje oluştur…", Subtitle = "Unity projesiyse Unity + VS Code, compose varsa Docker eklenir",
                Glyph = "",
                Run = () => PickFolder(owner, "Proje klasörünü seç", dir => addProject(TargetFactory.ProjectFromPath(dir)))
            }
        };

        foreach (var s in suggestions)
        {
            var sug = s;
            items.Add(new PickerItem
            {
                Group = sug.Group,
                Title = sug.Name,
                Subtitle = sug.Subtitle,
                Kind = sug.Targets.Count > 0 ? sug.Targets[0].Kind : null,
                Run = () => addProject(TargetFactory.NewProject(sug.Name, sug.Targets))
            });
        }
        return items;
    }

    // Proje düzenleyicideki "+ Ekle": tek bir hedef.
    // addChromePages verilirse tek sayfalar (açık sayfa / yer imi) mevcut Chrome hedefine sekme olarak eklenebilir.
    public static async Task<List<PickerItem>> ForTargets(Window owner, Action<LaunchTarget> add, Action<ChromePick>? addChromePages = null)
    {
        // Kaynaklar paralel yüklenir (Chrome taraması kendi içinde 3 sn ile sınırlı)
        var localTask = Task.Run(() => (UnityHubSource.Load(), VsCodeSource.Load(), BlenderRecent.Load()));
        var chromeTask = Task.Run(() => ChromeItems(owner, pick =>
        {
            if (!pick.IsGroup && addChromePages != null) addChromePages(pick);
            else add(pick.ToTarget());
        }, includeProfiles: true));
        var appsTask = AppCatalog.GetAppsAsync();
        await Task.WhenAll(localTask, chromeTask, appsTask);
        var (unity, vscode, blender) = localTask.Result;
        var chromeItems = chromeTask.Result;
        var apps = appsTask.Result;

        var items = new List<PickerItem>();

        foreach (var u in unity)
            items.Add(new PickerItem
            {
                Group = Suggestions.GroupUnity, Title = u.Title, Subtitle = $"Unity {u.Version}  ·  {u.Path}",
                Kind = TargetKind.Unity, Run = () => add(TargetFactory.Unity(u.Path))
            });

        foreach (var v in vscode)
            items.Add(new PickerItem
            {
                Group = GVsCode, Title = v.Name, Subtitle = v.Path,
                Kind = TargetKind.VsCode, Run = () => add(TargetFactory.VsCode(v.Path))
            });

        // Sıra: açık sayfalar ve klasörler üstte, uzun listeler (yer imleri, uygulamalar) altta
        items.AddRange(chromeItems.Where(i => i.Group is GChromeOpen or GChromeFolders));

        foreach (var b in blender)
            items.Add(new PickerItem
            {
                Group = GBlender, Title = Path.GetFileName(b), Subtitle = b,
                Kind = TargetKind.Blender, Run = () => add(TargetFactory.Blender(b))
            });
        items.Add(new PickerItem
        {
            Group = GBlender, Title = "Blender dosyası seç…", Subtitle = ".blend dosyası",
            Kind = TargetKind.Blender,
            Run = () => PickFile(owner, "Blender dosyası seç", "Blender dosyası (*.blend)|*.blend", f => add(TargetFactory.Blender(f)))
        });

        items.Add(Other("Unity projesi seç…", "Unity Hub'da olmayan bir proje klasörü", TargetKind.Unity,
            () => PickFolder(owner, "Unity proje klasörünü seç", d =>
            {
                if (!TargetFactory.IsUnityProject(d))
                    MessageBox.Show(owner, "Bu klasörde ProjectSettings\\ProjectVersion.txt yok; yine de eklendi.",
                        "devStarter", MessageBoxButton.OK, MessageBoxImage.Information);
                add(TargetFactory.Unity(d));
            })));
        items.Add(Other("VS Code ile klasör seç…", "Listede olmayan bir klasör", TargetKind.VsCode,
            () => PickFolder(owner, "VS Code'da açılacak klasörü seç", d => add(TargetFactory.VsCode(d)))));
        items.Add(Other("Visual Studio çözümü seç…", ".sln / .slnx", TargetKind.VisualStudio,
            () => PickFile(owner, "Çözüm seç", "Çözüm (*.sln;*.slnx)|*.sln;*.slnx",
                f => add(new LaunchTarget { Kind = TargetKind.VisualStudio, Path = f }))));
        items.Add(Other("Docker compose dosyası seç…", "Docker Desktop'ı başlatır, sonra compose up -d", TargetKind.Docker,
            () => PickFile(owner, "Compose dosyası seç", "Compose (*.yml;*.yaml)|*.yml;*.yaml", f => add(TargetFactory.Docker(f)))));
        items.Add(Other("Docker Desktop'ı başlat", "Sadece Docker motorunu ayağa kaldırır", TargetKind.Docker,
            () => add(new LaunchTarget { Kind = TargetKind.Docker })));
        items.Add(Other("Klasör (Gezgin'de aç)…", "Dosya Gezgini penceresi", TargetKind.Folder,
            () => PickFolder(owner, "Klasör seç", d => add(TargetFactory.Folder(d)))));
        items.Add(Other("Terminal…", "Seçilen klasörde Windows Terminal, istersen komutla", TargetKind.Terminal,
            () => PickFolder(owner, "Terminalin açılacağı klasör", d => add(TargetFactory.Terminal(d)))));
        items.Add(Other("Web adresi", "Varsayılan tarayıcıda açılır", TargetKind.Url,
            () => add(new LaunchTarget { Kind = TargetKind.Url, Path = "https://" })));
        items.Add(Other("Dosya seç…", "Varsayılan uygulamasıyla açılır", TargetKind.Url,
            () => PickFile(owner, "Dosya seç", "Tüm dosyalar (*.*)|*.*", f => add(TargetFactory.FromFile(f)))));
        items.Add(Other("Program seç (.exe)…", "Herhangi bir program veya kısayol", TargetKind.Custom,
            () => PickFile(owner, "Program seç", "Programlar (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|Tüm dosyalar (*.*)|*.*",
                f => add(new LaunchTarget { Kind = TargetKind.Custom, Path = f }))));

        items.AddRange(chromeItems.Where(i => i.Group is not (GChromeOpen or GChromeFolders)));

        foreach (var a in apps)
        {
            var path = TargetFactory.NormalizeAppPath(a.Path);
            var name = a.Name;
            items.Add(new PickerItem
            {
                Group = GApps, Title = name, IconPath = path, Kind = TargetKind.Custom,
                Run = () => add(new LaunchTarget { Kind = TargetKind.Custom, Path = path, Label = name })
            });
        }
        return items;
    }

    // Düzenleyicideki Chrome formu: "Açık sayfa / yer imi ekle"
    public static Task<List<PickerItem>> ForChrome(Window owner, Action<ChromePick> pick)
        => Task.Run(() => ChromeItems(owner, pick, includeProfiles: false));

    // Arka planda çalışır (UI Automation + dosya okuma); PickerItem'lar UI nesnesi içermez
    static List<PickerItem> ChromeItems(Window owner, Action<ChromePick> pick, bool includeProfiles)
    {
        var items = new List<PickerItem>();

        foreach (var page in ChromeWindows.Load())
        {
            var pg = page;
            var who = pg.ProfileName.Length > 0 ? pg.ProfileName : "profil bilinmiyor";
            items.Add(new PickerItem
            {
                Group = GChromeOpen, Title = pg.Title, Kind = TargetKind.Chrome,
                Subtitle = $"{Host(pg.Url)}  ·  {who}  ·  pencerede {pg.TabCount} sekme",
                Run = () => pick(new ChromePick(pg.ProfileDir, new List<string> { pg.Url }, pg.Title, false))
            });
        }
        items.Add(new PickerItem
        {
            Group = GChromeOpen, Title = "Bir pencerenin tüm sekmeleri nasıl eklenir?", Glyph = "",
            Subtitle = "Chrome'da Ctrl+Shift+D → bir klasöre kaydet → aşağıdan o klasörü seç",
            Run = () => MessageBox.Show(owner,
                "Chrome, çalışırken açık sekmelerin adreslerini dışarıya vermiyor; devStarter her pencerede " +
                "yalnızca o an görünen sayfayı okuyabiliyor.\n\n" +
                "Bir pencerenin tüm sekmelerini projeye bağlamak için:\n" +
                "1. O Chrome penceresinde Ctrl+Shift+D'ye bas (Tüm sekmeleri yer işaretlerine ekle).\n" +
                "2. Klasöre proje adını ver (ör. OyunProjem) ve kaydet.\n" +
                "3. Burada \"Chrome — yer imi klasörleri\" altından o klasörü seç.\n\n" +
                "Proje açılınca klasördeki tüm sayfalar tek pencerede sekme olarak açılır.",
                "devStarter", MessageBoxButton.OK, MessageBoxImage.Information)
        });

        var (bookmarks, folders) = ChromeBookmarks.Load();
        foreach (var f in folders)
        {
            var folder = f;
            items.Add(new PickerItem
            {
                Group = GChromeFolders, Title = folder.Name, Kind = TargetKind.Chrome,
                Subtitle = $"{folder.Urls.Count} sayfa tek pencerede  ·  {folder.ProfileName}  ·  {folder.Folder}",
                Run = () => pick(new ChromePick(folder.ProfileDir, folder.Urls.ToList(), folder.Name, true))
            });
        }
        foreach (var b in bookmarks)
        {
            var bm = b;
            items.Add(new PickerItem
            {
                Group = GChromeBookmarks, Title = bm.Name, Kind = TargetKind.Chrome,
                Subtitle = $"{Host(bm.Url)}  ·  {bm.ProfileName}  ·  {bm.Folder}",
                Run = () => pick(new ChromePick(bm.ProfileDir, new List<string> { bm.Url }, bm.Name, false))
            });
        }

        if (includeProfiles)
        {
            foreach (var c in ChromeProfiles.Load())
            {
                var profile = c;
                items.Add(new PickerItem
                {
                    Group = GChromeProfiles, Title = $"Chrome — {(profile.Name.Length > 0 ? profile.Name : profile.Dir)}",
                    Subtitle = $"{profile.Dir}  ·  sayfaları sonra formdan eklersin",
                    Kind = TargetKind.Chrome,
                    Run = () => pick(new ChromePick(profile.Dir, new List<string>(), "", true))
                });
            }
        }
        return items;
    }

    static string Host(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : url;

    static PickerItem Other(string title, string subtitle, TargetKind kind, Action run)
        => new() { Group = GOther, Title = title, Subtitle = subtitle, Kind = kind, Run = run };

    public static void PickFolder(Window owner, string title, Action<string> then)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (dlg.ShowDialog(owner) == true) then(dlg.FolderName);
    }

    public static void PickFile(Window owner, string title, string filter, Action<string> then)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        if (dlg.ShowDialog(owner) == true) then(dlg.FileName);
    }
}
