using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DevStarter.Launchers;
using DevStarter.Models;
using DevStarter.Services;
using DevStarter.Views;

namespace DevStarter;

public partial class MainWindow : Window
{
    const double CollapsedSize = 7;        // kapalıyken kenardaki şeridin kalınlığı
    const double CollapsedTopWidth = 180;  // üst konumda kapalı şeridin genişliği
    const double ShadowRoom = 24;          // gölge için pencereye eklenen pay

    readonly AppSettings settings = App.Settings;
    readonly ListCollectionView view;
    readonly DispatcherTimer watchTimer;
    readonly DispatcherTimer hideTimer;
    readonly DispatcherTimer toastTimer;
    Rect lastWorkArea;
    bool expanded = true;
    double expandedSize;
    int keepOpen;                          // açık menü / seçici / düzenleyici sayısı
    PickerWindow? picker;

    bool IsTop => settings.Dock == DockSide.Top;

    public MainWindow()
    {
        InitializeComponent();

        view = (ListCollectionView)CollectionViewSource.GetDefaultView(settings.Projects);
        view.CustomSort = new ProjectOrder(settings.Projects);
        view.Filter = FilterProject;
        ProjectList.ItemsSource = view;
        settings.Projects.CollectionChanged += (_, _) => UpdateEmpty();

        lastWorkArea = SystemParameters.WorkArea;
        ApplyLayout();
        UpdateEmpty();

        watchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        watchTimer.Tick += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            if (wa != lastWorkArea)
            {
                lastWorkArea = wa;
                ApplyLayout();
            }
        };
        watchTimer.Start();

        hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer.Stop();
            if (ShouldCollapse()) Collapse();
        };

        toastTimer = new DispatcherTimer();
        toastTimer.Tick += (_, _) =>
        {
            toastTimer.Stop();
            Toast.IsOpen = false;
        };

        Loaded += (_, _) => CollapseLater();
        Deactivated += (_, _) => RestartHide();
    }

    // Uygulama ikinci kez başlatılınca (kısayol, Başlat menüsü) kenar çubuğunu kısa süre göster
    public void Reveal()
    {
        Expand();
        CollapseLater();
    }

    void CollapseLater()
    {
        if (!settings.AutoHide) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (ShouldCollapse()) Collapse();
        };
        t.Start();
    }

    // ================= Yerleşim =================

    void ApplyLayout()
    {
        var wa = SystemParameters.WorkArea;
        bool top = IsTop;
        bool right = settings.Dock == DockSide.Right;
        bool cards = settings.View == ViewMode.Cards;

        Root.BeginAnimation(WidthProperty, null);
        Root.BeginAnimation(HeightProperty, null);

        ProjectList.ItemTemplate = (DataTemplate)FindResource(cards ? "CardTemplate" : "IconTemplate");
        ProjectList.ItemsPanel = (ItemsPanelTemplate)FindResource(top ? "HPanel" : "VPanel");
        ProjectList.ItemContainerStyle = (Style)FindResource(top ? "HItem" : "VItem");
        Scroller.VerticalScrollBarVisibility = top ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        Scroller.HorizontalScrollBarVisibility = top ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;

        SearchHost.Visibility = cards ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.SetDock(SearchHost, top ? Dock.Left : Dock.Top);
        DockPanel.SetDock(Footer, top ? Dock.Right : Dock.Bottom);
        LayoutFooter(top, cards);

        if (top)
        {
            double contentH = cards ? 54 : 52;
            Body.Width = double.NaN;
            Body.Height = contentH;
            expandedSize = contentH + 14;

            SearchHost.Width = 170;
            SearchHost.Margin = new Thickness(2, 0, 8, 0);
            SearchHost.VerticalAlignment = VerticalAlignment.Center;

            Root.HorizontalAlignment = HorizontalAlignment.Center;
            Root.VerticalAlignment = VerticalAlignment.Top;
            Root.MaxWidth = Math.Max(240, wa.Width - 80);
            Root.MaxHeight = double.PositiveInfinity;
            Root.CornerRadius = new CornerRadius(0, 0, 18, 18);
            Root.BorderThickness = new Thickness(1, 0, 1, 1);
            Root.Width = double.NaN;

            Handle.Width = 44;
            Handle.Height = 4;
            Handle.HorizontalAlignment = HorizontalAlignment.Center;
            Handle.VerticalAlignment = VerticalAlignment.Top;
            Handle.Margin = new Thickness(0, 1.5, 0, 0);

            Width = wa.Width;
            Height = expandedSize + ShadowRoom;
            Left = wa.Left;
            Top = wa.Top;
            Toast.Placement = PlacementMode.Bottom;
        }
        else
        {
            double contentW = cards ? 230 : 48;
            Body.Width = contentW;
            Body.Height = double.NaN;
            expandedSize = contentW + 14;

            SearchHost.Width = double.NaN;
            SearchHost.Margin = new Thickness(0, 0, 0, 6);
            SearchHost.VerticalAlignment = VerticalAlignment.Stretch;

            Root.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Root.VerticalAlignment = VerticalAlignment.Center;
            Root.MaxHeight = Math.Max(200, wa.Height - 40);
            Root.MaxWidth = double.PositiveInfinity;
            Root.CornerRadius = right ? new CornerRadius(18, 0, 0, 18) : new CornerRadius(0, 18, 18, 0);
            Root.BorderThickness = right ? new Thickness(1, 1, 0, 1) : new Thickness(0, 1, 1, 1);
            Root.Height = double.NaN;

            Handle.Width = 4;
            Handle.Height = 44;
            Handle.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Handle.VerticalAlignment = VerticalAlignment.Center;
            Handle.Margin = right ? new Thickness(0, 0, 1.5, 0) : new Thickness(1.5, 0, 0, 0);

            Width = expandedSize + ShadowRoom;
            Height = wa.Height;
            Top = wa.Top;
            Left = right ? wa.Right - Width : wa.Left;
            Toast.Placement = right ? PlacementMode.Left : PlacementMode.Right;
        }

        if (!settings.AutoHide) expanded = true;
        SetSizeImmediate(expanded);
        UpdateEmpty();
    }

    // Alt çubuk: [+ Yeni proje] [görünüm] [⋯]
    void LayoutFooter(bool top, bool cards)
    {
        static void Size(FrameworkElement el, double w, double h, Thickness m)
        {
            el.Width = w;
            el.Height = h;
            el.Margin = m;
        }

        ViewButton.Content = cards ? "" : "";
        ViewButton.ToolTip = cards ? "Sadece ikonlar (küçült)" : "Kartlar (isimlerle)";
        AddButton.Content = cards && !top ? "+   Yeni proje" : "+";
        AddButton.FontSize = cards && !top ? 13 : 20;
        Footer.VerticalAlignment = VerticalAlignment.Center;
        SmallButtons.Orientation = Orientation.Horizontal;

        if (top)
        {
            Footer.Orientation = Orientation.Horizontal;
            Footer.Margin = new Thickness(8, 0, 2, 0);
            Size(AddButton, 44, 44, new Thickness(0));
            Size(ViewButton, 34, 44, new Thickness(6, 0, 0, 0));
            Size(MenuButton, 34, 44, new Thickness(6, 0, 0, 0));
        }
        else if (cards)
        {
            Footer.Orientation = Orientation.Horizontal;
            Footer.Margin = new Thickness(0, 6, 0, 0);
            Size(AddButton, 230 - 2 * (36 + 6), 36, new Thickness(0));
            Size(ViewButton, 36, 36, new Thickness(6, 0, 0, 0));
            Size(MenuButton, 36, 36, new Thickness(6, 0, 0, 0));
        }
        else
        {
            // İkon modu (48 px): + üstte, altında iki küçük düğme yan yana
            Footer.Orientation = Orientation.Vertical;
            Footer.Margin = new Thickness(0, 6, 0, 0);
            Size(AddButton, 48, 40, new Thickness(0));
            Size(ViewButton, 21, 26, new Thickness(0, 6, 0, 0));
            Size(MenuButton, 21, 26, new Thickness(6, 6, 0, 0));
        }
    }

    void ViewToggle_Click(object sender, RoutedEventArgs e)
        => SetView(settings.View == ViewMode.Cards ? ViewMode.Icons : ViewMode.Cards);

    void MenuButton_Click(object sender, RoutedEventArgs e) => ShowAppMenu(MenuButton);

    void SetSizeImmediate(bool open)
    {
        Root.BeginAnimation(WidthProperty, null);
        Root.BeginAnimation(HeightProperty, null);
        Body.BeginAnimation(OpacityProperty, null);
        if (IsTop)
        {
            Root.Height = open ? expandedSize : CollapsedSize;
            Root.Width = open ? double.NaN : CollapsedTopWidth;
        }
        else
        {
            Root.Width = open ? expandedSize : CollapsedSize;
        }
        Body.Opacity = open ? 1 : 0;
        Handle.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    void Expand()
    {
        if (expanded) return;
        expanded = true;
        Animate(true);
    }

    void Collapse()
    {
        if (!expanded) return;
        expanded = false;
        Toast.IsOpen = false;
        Animate(false);
    }

    void Animate(bool open)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(open ? 230 : 200);
        Handle.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 220 : 110)));

        if (!IsTop)
        {
            Root.BeginAnimation(WidthProperty, new DoubleAnimation(open ? expandedSize : CollapsedSize, duration) { EasingFunction = ease });
            return;
        }

        // Üst konum: yükseklik + genişlik birlikte; açılınca genişlik içeriğe göre serbest kalır
        Root.BeginAnimation(HeightProperty, new DoubleAnimation(open ? expandedSize : CollapsedSize, duration) { EasingFunction = ease });
        double from = Root.ActualWidth;
        double to = CollapsedTopWidth;
        if (open)
        {
            Body.Measure(new Size(double.PositiveInfinity, Body.Height));
            to = Math.Min(Root.MaxWidth, Body.DesiredSize.Width + 14);
        }
        var widthAnim = new DoubleAnimation(from, to, duration) { EasingFunction = ease };
        if (open)
        {
            widthAnim.Completed += (_, _) =>
            {
                if (!expanded) return;
                Root.BeginAnimation(WidthProperty, null);
                Root.Width = double.NaN;
            };
        }
        Root.BeginAnimation(WidthProperty, widthAnim);
    }

    bool ShouldCollapse() => settings.AutoHide && expanded && keepOpen == 0 && !IsMouseOver
                             && Mouse.Captured == null && !(IsActive && SearchBox.IsKeyboardFocusWithin);

    void RestartHide()
    {
        hideTimer.Stop();
        if (settings.AutoHide) hideTimer.Start();
    }

    void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        hideTimer.Stop();
        Expand();
    }

    void Window_MouseLeave(object sender, MouseEventArgs e) => RestartHide();

    void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsTop) return;
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    void UpdateEmpty()
    {
        if (settings.Projects.Count == 0)
        {
            EmptyHint.Text = settings.View == ViewMode.Cards
                ? (IsTop ? "Henüz proje yok — + ile ekle" : "Henüz proje yok.\n+ ile ekle ya da bir klasörü buraya sürükle.")
                : "Proje\nekle";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else if (view.IsEmpty)
        {
            EmptyHint.Text = "Sonuç yok";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    // ================= Arama =================

    bool FilterProject(object o)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0 || o is not Project p) return true;
        var cmp = StringComparison.CurrentCultureIgnoreCase;
        return p.Name.Contains(q, cmp) || p.Targets.Any(t => t.DisplayName.Contains(q, cmp) || t.Path.Contains(q, cmp));
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        view.Refresh();
        UpdateEmpty();
    }

    void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var first = view.Cast<Project>().FirstOrDefault();
            if (first != null) _ = Launch(first, null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    // ================= Başlatma =================

    static Project? ProjectOf(object sender) => (sender as FrameworkElement)?.DataContext as Project;

    async void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (ProjectOf(sender) is not Project p) return;
        e.Handled = true;
        PlayJump(sender as FrameworkElement);
        await Launch(p, null);
    }

    // Karttaki küçük ikon: yalnızca o hedefi aç
    async void TargetIcon_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LaunchTarget t) return;
        var p = settings.Projects.FirstOrDefault(x => x.Targets.Contains(t));
        if (p == null) return;
        e.Handled = true;
        PlayJump(sender as FrameworkElement, 1.5);
        await Launch(p, new[] { t });
    }

    async Task Launch(Project p, IEnumerable<LaunchTarget>? only)
    {
        var results = await ProjectLauncher.LaunchAsync(p, only);
        if (results == null) return;
        if (results.Count == 0)
        {
            ShowToast(p.Name, "Bu projede etkin hedef yok. Sağ tık → Düzenle ile ekle.", true);
            return;
        }
        var notes = results.Where(r => !r.Ok || r.Message != null).ToList();
        if (notes.Count == 0) return;
        var body = string.Join("\n", notes.Select(r => $"{(r.Ok ? "✓" : "✕")} {r.Target.DisplayName}: {r.Message}"));
        ShowToast(p.Name, body, notes.Any(r => !r.Ok));
    }

    // macOS dock tarzı zıplama (Wsidebar'dan)
    static void PlayJump(FrameworkElement? fe, double? peak = null)
    {
        if (fe?.RenderTransform is not ScaleTransform st || st.IsFrozen) return;
        double top = peak ?? (fe is Border ? 1.05 : 1.3);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, JumpAnim(top));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, JumpAnim(top));
    }

    static DoubleAnimationUsingKeyFrames JumpAnim(double peak)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)))
        {
            EasingFunction = new BackEase { Amplitude = 1.4, EasingMode = EasingMode.EaseOut }
        });
        return anim;
    }

    // ================= Bildirim =================

    void ShowToast(string title, string body, bool error)
    {
        ToastTitle.Text = title;
        ToastBody.Text = body;
        ToastBorder.BorderBrush = new SolidColorBrush(error ? Color.FromArgb(0x90, 0xE5, 0x48, 0x4D) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        Toast.IsOpen = false;
        Toast.IsOpen = true;
        toastTimer.Stop();
        toastTimer.Interval = TimeSpan.FromSeconds(error ? 8 : 4);
        toastTimer.Start();
    }

    void Toast_Click(object sender, MouseButtonEventArgs e)
    {
        toastTimer.Stop();
        Toast.IsOpen = false;
    }

    // ================= Menüler =================

    static MenuItem Mi(string header, Action onClick, bool? isChecked = null, ImageSource? icon = null,
                       bool bold = false, bool enabled = true)
    {
        // Başlığı TextBlock olarak veriyoruz: alt çizgi (_) erişim tuşu sayılmasın
        var mi = new MenuItem { Header = new TextBlock { Text = header }, IsEnabled = enabled };
        if (isChecked != null)
        {
            mi.IsCheckable = true;
            mi.IsChecked = isChecked.Value;
        }
        if (icon != null) mi.Icon = new Image { Source = icon, Width = 16, Height = 16 };
        if (bold) mi.FontWeight = FontWeights.SemiBold;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    static readonly FontFamily MenuFont = new("Segoe UI Variable Text, Segoe UI");

    void OpenMenu(ContextMenu cm, UIElement target)
    {
        // ContextMenu yazı tipini PlacementTarget'tan devralır; hedef bir simge düğmesiyse
        // (Segoe Fluent Icons) menü metni kutucuk olarak çizilir. Bu yüzden açıkça veriyoruz.
        cm.FontFamily = MenuFont;
        cm.FontSize = 13;
        cm.FontWeight = FontWeights.Normal;
        cm.PlacementTarget = target;
        // Düğmeden açılan menüler düğmeye hizalansın, sağ tık menüleri fare konumunda açılsın
        cm.Placement = target is Button ? PlacementMode.Bottom : PlacementMode.MousePoint;
        keepOpen++;
        cm.Closed += (_, _) =>
        {
            keepOpen--;
            RestartHide();
        };
        cm.IsOpen = true;
    }

    void Item_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectOf(sender) is not Project p) return;
        e.Handled = true;
        ShowProjectMenu(p, (UIElement)sender);
    }

    // Kart üzerindeki ✎
    void CardEdit_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ProjectOf(sender) is Project p) EditProject(p, isNew: false);
    }

    // Kart üzerindeki ⋯
    void CardMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ProjectOf(sender) is Project p) ShowProjectMenu(p, (UIElement)sender);
    }

    // İkon modunda üzerine gelince çıkan kalem rozeti
    void IconEdit_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ProjectOf(sender) is Project p) EditProject(p, isNew: false);
    }

    void ShowProjectMenu(Project p, UIElement anchor)
    {
        var cm = new ContextMenu();
        cm.Items.Add(Mi("Aç", () => _ = Launch(p, null), bold: true));
        if (p.Targets.Count > 0)
        {
            var only = new MenuItem { Header = "Sadece…" };
            foreach (var t in p.Targets)
            {
                var target = t;
                only.Items.Add(Mi(t.DisplayName, () => _ = Launch(p, new[] { target }), icon: t.Icon));
            }
            cm.Items.Add(only);
        }
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi("Düzenle…", () => EditProject(p, isNew: false)));
        cm.Items.Add(Mi("Favori", () =>
        {
            p.IsFavorite = !p.IsFavorite;
            Save();
            view.Refresh();
        }, isChecked: p.IsFavorite));
        cm.Items.Add(Mi("Simge seç…", () => PickProjectIcon(p)));
        if (p.HasIcon)
            cm.Items.Add(Mi("Simgeyi kaldır", () => SetProjectIcon(p, "")));

        var folder = FirstFolder(p);
        if (folder != null)
            cm.Items.Add(Mi("Klasörü göster", () => ProcessService.Start("explorer.exe", ProcessService.Quote(folder))));

        int idx = settings.Projects.IndexOf(p);
        cm.Items.Add(Mi("Yukarı taşı", () => MoveProject(p, -1), enabled: idx > 0));
        cm.Items.Add(Mi("Aşağı taşı", () => MoveProject(p, 1), enabled: idx < settings.Projects.Count - 1));
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi("Sil", () => DeleteProject(p)));
        OpenMenu(cm, anchor);
    }

    void Root_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowAppMenu(Root);
    }

    void ShowAppMenu(UIElement anchor)
    {
        var cm = new ContextMenu();
        cm.Items.Add(Mi("Yeni proje…", OpenProjectPicker, bold: true));
        cm.Items.Add(Mi("Boş proje oluştur…", NewEmptyProject));
        cm.Items.Add(new Separator());

        var dock = new MenuItem { Header = "Konum" };
        dock.Items.Add(Mi("Sol", () => SetDock(DockSide.Left), settings.Dock == DockSide.Left));
        dock.Items.Add(Mi("Sağ", () => SetDock(DockSide.Right), settings.Dock == DockSide.Right));
        dock.Items.Add(Mi("Üst", () => SetDock(DockSide.Top), settings.Dock == DockSide.Top));
        cm.Items.Add(dock);

        var mode = new MenuItem { Header = "Görünüm" };
        mode.Items.Add(Mi("İkonlar", () => SetView(ViewMode.Icons), settings.View == ViewMode.Icons));
        mode.Items.Add(Mi("Kartlar", () => SetView(ViewMode.Cards), settings.View == ViewMode.Cards));
        cm.Items.Add(mode);

        cm.Items.Add(Mi("Otomatik gizle", ToggleAutoHide, settings.AutoHide));
        bool startup = StartupRegistration.IsEnabled();
        cm.Items.Add(Mi("Windows ile başlat", () => StartupRegistration.Set(!startup), startup));
        cm.Items.Add(new Separator());
        cm.Items.Add(Mi("Ayar klasörünü aç", () => ProcessService.ShellOpen(Log.Dir)));
        cm.Items.Add(Mi("Çıkış", () => Application.Current.Shutdown()));
        OpenMenu(cm, anchor);
    }

    void PickProjectIcon(Project p)
    {
        keepOpen++;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"{p.Name} — simge seç", Filter = ProjectIcons.FileFilter };
            if (dlg.ShowDialog(this) != true) return;
            var icon = ProjectIcons.Import(dlg.FileName);
            if (icon == null) ShowToast(p.Name, "Bu dosyadan simge oluşturulamadı.", true);
            else SetProjectIcon(p, icon);
        }
        finally
        {
            keepOpen--;
            RestartHide();
        }
    }

    void SetProjectIcon(Project p, string iconPath)
    {
        var old = p.IconPath;
        p.IconPath = iconPath;
        if (!string.Equals(old, iconPath, StringComparison.OrdinalIgnoreCase)) ProjectIcons.DeleteIfOwned(old);
        Save();
    }

    void SetDock(DockSide d)
    {
        settings.Dock = d;
        Save();
        Toast.IsOpen = false;
        expanded = true;
        ApplyLayout();
        RestartHide();
    }

    void SetView(ViewMode m)
    {
        settings.View = m;
        Save();
        expanded = true;
        ApplyLayout();
        RestartHide();
    }

    void ToggleAutoHide()
    {
        settings.AutoHide = !settings.AutoHide;
        Save();
        if (!settings.AutoHide) Expand();
        else RestartHide();
    }

    // ================= Proje işlemleri =================

    void AddClick(object sender, RoutedEventArgs e) => OpenProjectPicker();

    void OpenProjectPicker()
    {
        if (picker != null)
        {
            picker.Activate();
            return;
        }
        var w = new PickerWindow("Proje ara: Unity Hub, VS Code klasörleri…",
            () => PickerSources.ForProjects(this, settings.Projects, AddProject, NewEmptyProject),
            PickerPosition) { Owner = this };
        keepOpen++;
        w.Closed += (_, _) =>
        {
            picker = null;
            keepOpen--;
            RestartHide();
        };
        picker = w;
        w.Show();
    }

    Point PickerPosition(Size s)
    {
        var wa = SystemParameters.WorkArea;
        double y = wa.Top + Math.Max(0, (wa.Height - s.Height) / 2);
        return settings.Dock switch
        {
            DockSide.Right => new Point(wa.Right - expandedSize - s.Width + 8, y),
            DockSide.Left => new Point(wa.Left + expandedSize - 8, y),
            _ => new Point(wa.Left + (wa.Width - s.Width) / 2, wa.Top + expandedSize - 8)
        };
    }

    void AddProject(Project p)
    {
        settings.Projects.Add(p);
        Save();
        view.Refresh();
        Expand();
        RestartHide();
        var kinds = string.Join(" + ", p.Targets.Select(t => t.KindName).Distinct());
        ShowToast(p.Name, kinds.Length > 0 ? $"Eklendi — tıklayınca {kinds} açılır." : "Eklendi.", false);
    }

    void NewEmptyProject() => EditProject(new Project(), isNew: true);

    void EditProject(Project p, bool isNew)
    {
        keepOpen++;
        bool ok;
        bool delete;
        try
        {
            var w = new ProjectEditorWindow(p, isNew) { Owner = this };
            ok = w.ShowDialog() == true;
            delete = w.DeleteRequested;
        }
        finally
        {
            keepOpen--;
        }
        if (delete)
        {
            RemoveProject(p);
        }
        else if (ok)
        {
            if (isNew) settings.Projects.Add(p);
            Save();
            view.Refresh();
        }
        RestartHide();
    }

    void MoveProject(Project p, int delta)
    {
        int i = settings.Projects.IndexOf(p);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= settings.Projects.Count) return;
        settings.Projects.Move(i, j);
        view.Refresh();
        Save();
    }

    void DeleteProject(Project p)
    {
        keepOpen++;
        var answer = MessageBox.Show(this, $"\"{p.Name}\" projesi silinsin mi?\n(Dosyalarına dokunulmaz, sadece listeden kalkar.)",
            "devStarter", MessageBoxButton.YesNo, MessageBoxImage.Question);
        keepOpen--;
        if (answer == MessageBoxResult.Yes) RemoveProject(p);
        RestartHide();
    }

    void RemoveProject(Project p)
    {
        settings.Projects.Remove(p);
        ProjectIcons.DeleteIfOwned(p.IconPath);
        Save();
        ShowToast(p.Name, "Silindi.", false);
    }

    static string? FirstFolder(Project p)
    {
        foreach (var t in p.Targets)
        {
            if (t.Path.Length == 0 || t.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(t.Path)) return t.Path;
            if (File.Exists(t.Path)) return Path.GetDirectoryName(t.Path);
        }
        return null;
    }

    void Save() => SettingsStore.Save(settings);

    // ================= Sürükle-bırak =================

    static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    void Window_DragEnter(object sender, DragEventArgs e)
    {
        hideTimer.Stop();
        Expand();
        Window_DragOver(sender, e);
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_DragLeave(object sender, DragEventArgs e) => RestartHide();

    // Boş alana bırakılan klasör/dosyalar → yeni proje
    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) continue;
            AddProject(TargetFactory.ProjectFromPath(path));
        }
    }

    void Item_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // Bir projenin üzerine bırakılanlar → o projeye hedef olarak eklenir
    void Item_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ProjectOf(sender) is not Project p || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        // Resim bırakılırsa proje simgesi olur
        var files = paths.Where(File.Exists).ToList();
        var image = files.FirstOrDefault(ProjectIcons.IsImageFile);
        if (image != null)
        {
            var icon = ProjectIcons.Import(image);
            if (icon != null)
            {
                SetProjectIcon(p, icon);
                ShowToast(p.Name, "Simge güncellendi.", false);
            }
            files.Remove(image);
        }

        int added = 0;
        foreach (var file in files)
        {
            p.Targets.Add(TargetFactory.FromFile(file));
            added++;
        }
        if (added > 0)
        {
            Save();
            ShowToast(p.Name, $"{added} hedef eklendi.", false);
        }

        var dir = paths.FirstOrDefault(Directory.Exists);
        if (dir != null)
        {
            var anchor = (UIElement)sender;
            Dispatcher.BeginInvoke(() => AskFolderKind(p, dir, anchor));
        }
    }

    void AskFolderKind(Project p, string dir, UIElement anchor)
    {
        void Add(LaunchTarget t)
        {
            p.Targets.Add(t);
            Save();
            ShowToast(p.Name, $"{t.DisplayName} eklendi.", false);
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        var cm = new ContextMenu();
        cm.Items.Add(new MenuItem { Header = new TextBlock { Text = $"\"{name}\" nasıl açılsın?" }, IsEnabled = false });
        if (TargetFactory.IsUnityProject(dir))
            cm.Items.Add(Mi("Unity projesi", () => Add(TargetFactory.Unity(dir)), icon: KindInfo.KindIcon(TargetKind.Unity)));
        cm.Items.Add(Mi("VS Code", () => Add(TargetFactory.VsCode(dir)), icon: KindInfo.KindIcon(TargetKind.VsCode)));
        cm.Items.Add(Mi("Klasör (Gezgin)", () => Add(TargetFactory.Folder(dir)), icon: KindInfo.KindIcon(TargetKind.Folder)));
        cm.Items.Add(Mi("Terminal", () => Add(TargetFactory.Terminal(dir)), icon: KindInfo.KindIcon(TargetKind.Terminal)));
        var compose = TargetFactory.FindCompose(dir);
        if (compose != null)
            cm.Items.Add(Mi("Docker compose", () => Add(TargetFactory.Docker(compose)), icon: KindInfo.KindIcon(TargetKind.Docker)));
        OpenMenu(cm, anchor);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Save();
    }

    // Favoriler önce, sonra kullanıcının sırası
    sealed class ProjectOrder(IList<Project> source) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not Project a || y is not Project b) return 0;
            int fav = b.IsFavorite.CompareTo(a.IsFavorite);
            return fav != 0 ? fav : source.IndexOf(a).CompareTo(source.IndexOf(b));
        }
    }
}
