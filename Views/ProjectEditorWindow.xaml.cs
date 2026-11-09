using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevStarter.Discovery;
using DevStarter.Launchers;
using DevStarter.Models;
using DevStarter.Services;
using Microsoft.Win32;

namespace DevStarter.Views;

// Projeyi bir kopya üzerinde düzenler; "Kaydet" ile asıl projeye yazar
public partial class ProjectEditorWindow : Window
{
    readonly Project original;
    readonly Project draft;
    readonly string originalIcon;
    readonly List<string> createdIcons = new();
    bool saved;

    // Kullanıcı "Projeyi sil"i onayladıysa true (silme işlemini çağıran pencere yapar)
    public bool DeleteRequested { get; private set; }

    public ProjectEditorWindow(Project project, bool isNew)
    {
        InitializeComponent();
        original = project;
        originalIcon = project.IconPath;
        draft = project.Clone();
        if (draft.Color.Length == 0) draft.Color = Palette.ForName(draft.Name);
        DeleteProjectButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        Closed += (_, _) => CleanupIcons();

        DataContext = draft;
        Title = isNew ? "Yeni proje — devStarter" : $"{project.Name} — devStarter";
        Swatches.ItemsSource = Palette.Swatches;
        ProfileBox.ItemsSource = ChromeProfiles.Load();
        TargetList.ItemsSource = draft.Targets;
        draft.Targets.CollectionChanged += (_, _) => UpdateEmpty();

        if (draft.Targets.Count > 0) TargetList.SelectedIndex = 0;
        UpdateEmpty();
        UpdateForm();

        Loaded += (_, _) =>
        {
            if (isNew) NameBox.Focus();
        };
    }

    void UpdateEmpty() => EmptyList.Visibility = draft.Targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    LaunchTarget? Selected => TargetList.SelectedItem as LaunchTarget;

    void TargetList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateForm();

    void UpdateForm()
    {
        var t = Selected;
        EmptyForm.Visibility = t == null ? Visibility.Visible : Visibility.Collapsed;
        FormScroll.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        if (t == null) return;

        var k = t.Kind;
        Show(PathRow, k != TargetKind.Chrome);
        PathLabel.Text = k switch
        {
            TargetKind.Unity => "Unity proje klasörü",
            TargetKind.VsCode => "Klasör veya .code-workspace dosyası",
            TargetKind.VisualStudio => "Çözüm (.sln) veya klasör",
            TargetKind.Blender => ".blend dosyası (boş = sadece Blender'ı aç)",
            TargetKind.Docker => "Compose dosyası veya klasör (boş = sadece Docker Desktop)",
            TargetKind.Folder => "Klasör",
            TargetKind.Terminal => "Başlangıç klasörü",
            TargetKind.Url => "Web adresi veya dosya",
            TargetKind.Custom => "Program, kısayol veya dosya",
            _ => "Yol"
        };
        BrowseButton.Visibility = k == TargetKind.Url && KindInfo.IsWebUrl(t.Path) ? Visibility.Collapsed : Visibility.Visible;

        Show(UrlsRow, k == TargetKind.Chrome);
        Show(ProfileRow, k == TargetKind.Chrome);

        Show(ArgsRow, k is not (TargetKind.Folder or TargetKind.Url));
        ArgsLabel.Text = k switch
        {
            TargetKind.Terminal => "Çalıştırılacak komut (isteğe bağlı)",
            TargetKind.Docker => "Docker komutu (isteğe bağlı)",
            _ => "Ek argümanlar (isteğe bağlı)"
        };
        ArgsHint.Text = k switch
        {
            TargetKind.Docker => "Boş bırakırsan: docker compose up -d. Örnek: compose -f dev.yml up -d",
            TargetKind.Terminal => "Örnek: npm run dev",
            TargetKind.Unity => "Örnek: -buildTarget Android",
            TargetKind.Chrome => "Örnek: --incognito",
            _ => ""
        };
        ArgsHint.Visibility = ArgsHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Show(WorkDirRow, k == TargetKind.Custom);
        Show(ExeRow, k is TargetKind.Unity or TargetKind.VsCode or TargetKind.VisualStudio or TargetKind.Chrome or TargetKind.Blender);

        TestResult.Text = "";
        UpdateHints();
    }

    void UpdateHints()
    {
        var t = Selected;
        if (t == null) return;

        PathHint.Text = t.Kind switch
        {
            TargetKind.Unity => UnityInfo(t.Path),
            TargetKind.Url or TargetKind.Custom or TargetKind.Blender or TargetKind.VisualStudio
                when t.Path.Length > 0 && !KindInfo.IsWebUrl(t.Path) && !t.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                     && !File.Exists(t.Path) && !Directory.Exists(t.Path) => "⚠ Bu yol bulunamadı",
            TargetKind.VsCode or TargetKind.Folder or TargetKind.Terminal
                when t.Path.Length > 0 && !File.Exists(t.Path) && !Directory.Exists(t.Path) => "⚠ Bu yol bulunamadı",
            _ => ""
        };
        PathHint.Visibility = PathHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (ExeRow.Visibility == Visibility.Visible)
        {
            string? auto = t.Kind switch
            {
                TargetKind.Unity => UnityLauncher.ReadVersion(t.Path) is string v ? ToolLocator.UnityEditor(v) : null,
                TargetKind.VsCode => ToolLocator.Find(ToolLocator.Code),
                TargetKind.VisualStudio => ToolLocator.Find(ToolLocator.VisualStudio),
                TargetKind.Chrome => ToolLocator.Find(ToolLocator.Chrome),
                TargetKind.Blender => ToolLocator.Find(ToolLocator.Blender),
                _ => null
            };
            ExeHint.Text = auto != null ? $"Otomatik: {auto}" : "Otomatik bulunamadı — gerekirse program yolunu seç.";
        }
    }

    static string UnityInfo(string path)
    {
        if (path.Length == 0) return "";
        if (!Directory.Exists(path)) return "⚠ Klasör bulunamadı";
        var v = UnityLauncher.ReadVersion(path);
        if (v == null) return "⚠ ProjectSettings/ProjectVersion.txt yok — Unity projesi değil gibi görünüyor.";
        return ToolLocator.UnityEditor(v) != null
            ? $"Editör sürümü {v} — yüklü ✓"
            : $"Editör sürümü {v} — yüklü değil ✕ (açarken Unity Hub açılır)";
    }

    void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Selected is { Kind: TargetKind.Url } t)
            BrowseButton.Visibility = KindInfo.IsWebUrl(t.Path) ? Visibility.Collapsed : Visibility.Visible;
        UpdateHints();
    }

    static void Show(UIElement el, bool visible) => el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    // ---- Gözat ----

    void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not LaunchTarget t) return;
        void Set(string p) => t.Path = p;
        switch (t.Kind)
        {
            case TargetKind.Unity:
            case TargetKind.VsCode:
            case TargetKind.Folder:
            case TargetKind.Terminal:
                PickerSources.PickFolder(this, "Klasör seç", Set);
                break;
            case TargetKind.VisualStudio:
                PickerSources.PickFile(this, "Çözüm seç", "Çözüm (*.sln;*.slnx)|*.sln;*.slnx", Set);
                break;
            case TargetKind.Blender:
                PickerSources.PickFile(this, "Blender dosyası seç", "Blender dosyası (*.blend)|*.blend", Set);
                break;
            case TargetKind.Docker:
                PickerSources.PickFile(this, "Compose dosyası seç", "Compose (*.yml;*.yaml)|*.yml;*.yaml", Set);
                break;
            case TargetKind.Custom:
                PickerSources.PickFile(this, "Program seç", "Programlar (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|Tüm dosyalar (*.*)|*.*", Set);
                break;
            default:
                PickerSources.PickFile(this, "Dosya seç", "Tüm dosyalar (*.*)|*.*", Set);
                break;
        }
        UpdateHints();
    }

    void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not LaunchTarget t) return;
        var dlg = new OpenFileDialog { Title = "Program seç", Filter = "Programlar (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) == true) t.ExeOverride = dlg.FileName;
        UpdateHints();
    }

    // ---- Liste işlemleri ----

    void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        var picker = new PickerWindow("Ne açılsın? Unity, VS Code, Chrome sayfası, yer imi, Docker…",
            () => PickerSources.ForTargets(this, AddTarget, AddChromePages)) { Owner = this };
        picker.Show();
    }

    // Tek Chrome sayfası: aynı profildeki mevcut Chrome hedefine sekme olarak eklenir (tek pencere)
    void AddChromePages(ChromePick pick)
    {
        var existing = draft.Targets.FirstOrDefault(t =>
            t.Kind == TargetKind.Chrome && t.Label.Length == 0 && t.ChromeProfile == pick.ProfileDir);
        if (existing == null)
        {
            AddTarget(pick.ToTarget());
            return;
        }
        AppendUrls(existing, pick);
        TargetList.SelectedItem = existing;
        TestResult.Text = "Mevcut Chrome penceresine sekme olarak eklendi.";
    }

    void AddChromePage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Kind: TargetKind.Chrome } t) return;
        CommitFocused();
        var picker = new PickerWindow("Chrome sayfası veya yer imi ara…",
            () => PickerSources.ForChrome(this, pick =>
            {
                AppendUrls(t, pick);
                TestResult.Text = pick.ProfileDir.Length > 0 && t.ChromeProfile != pick.ProfileDir
                    ? $"Eklendi. Not: sayfa başka bir profilden; bu hedefin profiliyle ({ChromeProfiles.NameOf(t.ChromeProfile)}) açılacak."
                    : "Eklendi.";
            })) { Owner = this };
        picker.Show();
    }

    static void AppendUrls(LaunchTarget t, ChromePick pick)
    {
        var urls = t.Urls.ToList();
        foreach (var u in pick.Urls)
            if (!urls.Contains(u, StringComparer.OrdinalIgnoreCase)) urls.Add(u);
        t.Urls = urls;
        if (t.ChromeProfile.Length == 0) t.ChromeProfile = pick.ProfileDir;
    }

    // ---- Simge ----

    void IconBadge_Click(object sender, MouseButtonEventArgs e) => PickIcon_Click(sender, e);

    void PickIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Proje simgesi seç", Filter = ProjectIcons.FileFilter };
        if (dlg.ShowDialog(this) == true) SetIcon(ProjectIcons.Import(dlg.FileName));
    }

    void PasteIcon_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            {
                SetIcon(ProjectIcons.Save(img));
                return;
            }
            if (Clipboard.ContainsFileDropList())
            {
                var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(ProjectIcons.IsImageFile);
                if (file != null)
                {
                    SetIcon(ProjectIcons.Import(file));
                    return;
                }
            }
        }
        catch (Exception ex) { Log.Error(ex, "paste icon"); }
        MessageBox.Show(this, "Panoda resim yok. Bir resmi kopyalayıp (ör. Ctrl+C) tekrar dene.", "devStarter",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void RemoveIcon_Click(object sender, RoutedEventArgs e) => draft.IconPath = "";

    void IconBadge_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void IconBadge_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            SetIcon(ProjectIcons.Import(files[0]));
    }

    void SetIcon(string? path)
    {
        if (path == null)
        {
            MessageBox.Show(this, "Bu dosyadan simge oluşturulamadı.", "devStarter", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        createdIcons.Add(path);
        draft.IconPath = path;
    }

    // Kaydedilmeyen simge dosyalarını ve değiştirilen eski simgeyi temizler
    void CleanupIcons()
    {
        var keep = saved ? original.IconPath : originalIcon;
        foreach (var file in createdIcons.Where(f => !string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)))
            ProjectIcons.DeleteIfOwned(file);
        if (saved && !string.Equals(originalIcon, original.IconPath, StringComparison.OrdinalIgnoreCase))
            ProjectIcons.DeleteIfOwned(originalIcon);
    }

    void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            $"\"{original.Name}\" projesi silinsin mi?\n(Dosyalarına dokunulmaz, sadece listeden kalkar.)",
            "devStarter", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        DeleteRequested = true;
        DialogResult = false;
    }

    void AddTarget(LaunchTarget t)
    {
        draft.Targets.Add(t);
        TargetList.SelectedItem = t;
        TargetList.ScrollIntoView(t);
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            draft.Name = TargetFactory.SuggestName(t);
            draft.Color = Palette.ForName(draft.Name);
        }
    }

    void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    void Move(int delta)
    {
        if (Selected is not LaunchTarget t) return;
        int i = draft.Targets.IndexOf(t);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= draft.Targets.Count) return;
        draft.Targets.Move(i, j);
        TargetList.SelectedItem = t;
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not LaunchTarget t) return;
        int i = draft.Targets.IndexOf(t);
        draft.Targets.Remove(t);
        if (draft.Targets.Count > 0) TargetList.SelectedIndex = Math.Min(i, draft.Targets.Count - 1);
    }

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not LaunchTarget t) return;
        CommitFocused();
        TestResult.Text = "Açılıyor…";
        var probe = t.Clone();
        probe.DelayMs = 0;
        var r = await ProjectLauncher.RunOne(probe);
        t.Error = r.Ok ? null : r.Message;
        TestResult.Text = r.Ok ? "✓ Açıldı" + (r.Message != null ? $" — {r.Message}" : "") : "✕ " + r.Message;
    }

    // ---- Kaydet ----

    void CommitFocused()
    {
        if (Keyboard.FocusedElement is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitFocused();
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            if (draft.Targets.Count > 0)
            {
                draft.Name = TargetFactory.SuggestName(draft.Targets[0]);
            }
            else
            {
                MessageBox.Show(this, "Projeye bir ad ver ya da en az bir hedef ekle.", "devStarter",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                NameBox.Focus();
                return;
            }
        }
        draft.Name = draft.Name.Trim();
        if (draft.Color.Length == 0) draft.Color = Palette.ForName(draft.Name);
        foreach (var t in draft.Targets) t.Error = null;

        original.CopyFrom(draft);
        saved = true;
        DialogResult = true;
    }
}
