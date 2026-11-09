using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DevStarter.Agents;
using DevStarter.Models;
using DevStarter.Services;
using DevStarter.Usage;

namespace DevStarter.Views;

public partial class AgentsWindow : Window
{
    static readonly string[] KindNames = { "Tüm ajanlar", "Claude", "Codex", "Gemini", "OpenCode" };

    readonly AgentMonitor monitor = AgentMonitor.Instance;
    readonly AppSettings settings = App.Settings;
    readonly ListCollectionView view;
    readonly DispatcherTimer tick;
    Project? projectFilter;

    public AgentsWindow()
    {
        InitializeComponent();
        RestoreSavedBounds();

        view = new ListCollectionView(monitor.Sessions);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AgentSession.GroupName)));
        view.SortDescriptions.Add(new SortDescription(nameof(AgentSession.GroupOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(AgentSession.StateOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(AgentSession.LastActivity), ListSortDirection.Descending));
        view.IsLiveSorting = true;
        view.LiveSortingProperties.Add(nameof(AgentSession.GroupOrder));
        view.LiveSortingProperties.Add(nameof(AgentSession.StateOrder));
        view.IsLiveGrouping = true;
        view.LiveGroupingProperties.Add(nameof(AgentSession.GroupName));
        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(AgentSession.State));
        view.LiveFilteringProperties.Add(nameof(AgentSession.Project));
        view.Filter = Filter;
        SessionList.ItemsSource = view;

        KindFilter.ItemsSource = KindNames;
        KindFilter.SelectedIndex = 0;

        monitor.Sessions.CollectionChanged += Sessions_Changed;
        monitor.PropertyChanged += Monitor_PropertyChanged;

        // "5 dk süredir çalışıyor" metinleri tazelensin
        tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        tick.Tick += (_, _) =>
        {
            foreach (var s in monitor.Sessions) s.Tick();
        };
        tick.Start();

        SourcesHint.Text = "Claude Code oturumları ayar gerektirmeden izlenir. "
                           + "Codex, Gemini ve OpenCode için Bağlantılar sekmesinden bağlantı kur.";
        UpdateSummary();

        LinkList.ItemsSource = links;
        NotifyWaiting.IsChecked = settings.Agents.NotifyOnWaiting;
        NotifyFinished.IsChecked = settings.Agents.NotifyOnFinished;
        RefreshLinks();
        tick.Tick += (_, _) =>
        {
            RefreshLinks();
            usage.Tick();
        };
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != Tabs) return;
            if (Tabs.SelectedItem == LinksTab) RefreshLinks();
            if (Tabs.SelectedItem == UsageTab && !usageLoaded)
            {
                usageLoaded = true;
                _ = RefreshUsage(force: true);
            }
        };

        UsageList.ItemsSource = usage.Cards;
        UpdateUsageInfo();
        // Panel açıkken yerel kaynaklar 2 dk'da bir, Codex 10 dk'da bir yenilenir
        usageTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        usageTimer.Tick += (_, _) => { if (usageLoaded) _ = RefreshUsage(force: false); };
        usageTimer.Start();
    }

    // ---------- Limitler ----------

    readonly UsageService usage = UsageService.Instance;
    readonly DispatcherTimer usageTimer;
    bool usageLoaded;

    public void ShowUsage()
    {
        Tabs.SelectedItem = UsageTab;
    }

    async Task RefreshUsage(bool force)
    {
        RefreshUsageButton.IsEnabled = false;
        try { await usage.RefreshAsync(force); }
        catch (Exception ex) { Log.Error(ex, "usage refresh"); }
        finally
        {
            RefreshUsageButton.IsEnabled = true;
            UpdateUsageInfo();
        }
    }

    void RefreshUsage_Click(object sender, RoutedEventArgs e) => _ = RefreshUsage(force: true);

    void UpdateUsageInfo()
    {
        UsageInfo.Text = "Claude değeri Claude Code'un son kaydından okunur (ağ isteği yok). "
                         + "Codex resmî app-server'dan, Antigravity açıkken kendi yerel sunucusundan okunur."
                         + (usage.LastRefresh is DateTime t ? $" Son yenileme: {t:HH:mm}." : "");
    }

    // ---------- Bağlantılar ----------

    readonly List<LinkRow> links = new[] { AgentKind.Claude, AgentKind.Codex, AgentKind.Gemini, AgentKind.OpenCode }
        .Select(k => new LinkRow(k)).ToList();

    void RefreshLinks()
    {
        foreach (var row in links)
            row.Refresh(AgentIntegrations.Status(row.Kind), monitor.LastHookEvent(row.Kind));
        var hooks = monitor.Hooks;
        HookStatusText.Text = hooks.Running
            ? $"{hooks.Status}. Hook'lar durum bilgisini yalnızca bu bilgisayardaki bu adrese gönderir; devStarter kapalıyken hiçbir şey göndermez."
            : $"{hooks.Status} Codex, Gemini ve OpenCode durumları bu sorun çözülene kadar görünmez.";
    }

    void LinkAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LinkRow row) return;
        try
        {
            if (row.Installed)
            {
                AgentIntegrations.Uninstall(row.Kind);
            }
            else
            {
                var what = row.Kind switch
                {
                    AgentKind.Codex => "Codex'in ~/.codex/hooks.json dosyasına devStarter hook'ları eklenecek (Codex'i yavaşlatmamaları için arka planda çalışırlar).\n\nCodex yeni hook'ları sen onaylamadan çalıştırmaz: kurulumdan sonra Codex'te /hooks yazıp devStarter hook'larını onayla.",
                    AgentKind.Gemini => "Gemini CLI'ın ~/.gemini/settings.json dosyasına devStarter hook'ları eklenecek.",
                    _ => "OpenCode'a ~/.config/opencode/plugins/devstarter-status.js eklentisi eklenecek. Açık OpenCode pencereleri yeniden başlatılınca devreye girer."
                };
                var answer = MessageBox.Show(this, what + "\n\nDeğiştirilen dosya önce %APPDATA%\\devStarter\\backups altına yedeklenir. Devam edilsin mi?",
                    $"{row.Name} bağlantısı", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) return;
                AgentIntegrations.Install(row.Kind, settings.Agents.HookPort);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"integration {row.Kind}");
            MessageBox.Show(this, $"{row.Name} ayar dosyası değiştirilemedi:\n{ex.Message}", "devStarter",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshLinks();
    }

    void Notify_Changed(object sender, RoutedEventArgs e)
    {
        settings.Agents.NotifyOnWaiting = NotifyWaiting.IsChecked == true;
        settings.Agents.NotifyOnFinished = NotifyFinished.IsChecked == true;
        SettingsStore.Save(settings);
    }

    // Kenar çubuğundaki proje rozetinden açıldığında yalnızca o proje gösterilir
    public void ShowProject(Project? p)
    {
        projectFilter = p;
        ProjectChip.Visibility = p != null ? Visibility.Visible : Visibility.Collapsed;
        ProjectChipText.Text = p != null ? $"Proje: {p.Name}" : "";
        Tabs.SelectedIndex = 0;
        view.Refresh();
        UpdateSummary();
    }

    bool Filter(object o)
    {
        if (o is not AgentSession s) return false;
        if (projectFilter != null && s.Project != projectFilter) return false;
        if (WaitingOnly.IsChecked == true && s.State != AgentState.Waiting) return false;
        int k = KindFilter.SelectedIndex;
        if (k > 0 && (int)s.Kind != k - 1) return false;
        return true;
    }

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (view == null) return;
        view.Refresh();
        UpdateSummary();
    }

    void ClearProject_Click(object sender, RoutedEventArgs e) => ShowProject(null);

    void Sessions_Changed(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateSummary();

    void Monitor_PropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateSummary();

    void UpdateSummary()
    {
        var shown = monitor.Sessions.Where(s => projectFilter == null || s.Project == projectFilter).ToList();
        int w = shown.Count(s => s.State == AgentState.Working);
        int wa = shown.Count(s => s.State == AgentState.Waiting);
        int i = shown.Count(s => s.State == AgentState.Idle);

        SummaryText.Text = shown.Count == 0 ? "Açık oturum yok" : $"{shown.Count} açık oturum";
        var parts = new List<string>();
        if (wa > 0) parts.Add($"{wa} seni bekliyor");
        if (w > 0) parts.Add($"{w} çalışıyor");
        if (i > 0) parts.Add($"{i} boşta");
        SummaryParts.Text = string.Join(" · ", parts);
        SummaryParts.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool empty = view.IsEmpty;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = monitor.Sessions.Count == 0
            ? "Şu an açık bir ajan oturumu yok.\nClaude Code, Codex, Gemini veya OpenCode'da bir oturum açınca burada görünür."
            : "Bu filtreye uyan oturum yok.";
    }

    void Session_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentSession s) return;
        if (!ProcessUtil.Focus(s))
            MessageBox.Show(this, $"{s.KindName} oturumunun penceresi bulunamadı.\n{s.Cwd}", "devStarter",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- konum / boyut ----------

    void RestoreSavedBounds()
    {
        var b = settings.AgentsWindow;
        if (b == null || b.Width < MinWidth || b.Height < MinHeight) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(b.Left, b.Top, b.Width, b.Height);
        if (!screen.IntersectsWith(rect)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (WindowState == WindowState.Normal)
        {
            settings.AgentsWindow = new WindowBounds { Left = Left, Top = Top, Width = Width, Height = Height };
            SettingsStore.Save(settings);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        tick.Stop();
        usageTimer.Stop();
        monitor.Sessions.CollectionChanged -= Sessions_Changed;
        monitor.PropertyChanged -= Monitor_PropertyChanged;
        view.DetachFromSourceCollection();
    }
}

// Bağlantılar sekmesindeki bir ajan satırı
public sealed class LinkRow(AgentKind kind) : ObservableObject
{
    IntegrationStatus status = new(kind, false, false, "");
    DateTime? lastEvent;

    public AgentKind Kind { get; } = kind;
    public string Name => AgentText.KindName(Kind);
    public string Glyph => AgentText.KindGlyph(Kind);
    public System.Windows.Media.Brush KindBrush => AgentColors.Kind(Kind);
    public bool Installed => status.Installed;
    public string Detail => status.Detail;
    public bool CanAct => Kind != AgentKind.Claude && status.AppFound;
    public string ActionText => status.Installed ? "Kaldır" : "Bağla";

    public string StatusText =>
        !status.AppFound ? "Bulunamadı"
        : Kind == AgentKind.Claude ? "Otomatik"
        : !status.Installed ? "Bağlı değil"
        : lastEvent != null ? "Çalışıyor"
        : "Kurulu";

    public System.Windows.Media.Brush StatusBrush =>
        !status.AppFound || !status.Installed ? AgentColors.Idle
        : Kind == AgentKind.Claude || lastEvent != null ? AgentColors.Working
        : AgentColors.Waiting;

    public string LastEventText =>
        lastEvent is DateTime t ? $"Son durum bildirimi {AgentText.Duration(DateTime.Now - t)} önce geldi."
        : !status.Installed || Kind == AgentKind.Claude ? ""
        : Kind == AgentKind.Codex ? "Henüz bildirim gelmedi. Codex'te /hooks yazıp devStarter hook'larını onayladığından emin ol; sonra Codex'e bir şey yaz."
        : $"Henüz bildirim gelmedi. {Name} oturumunu yeniden başlatıp bir şey yazınca gelir.";

    public bool HasLastEventText => LastEventText.Length > 0;

    public void Refresh(IntegrationStatus s, DateTime? last)
    {
        status = s;
        lastEvent = last;
        foreach (var p in new[] { nameof(Installed), nameof(Detail), nameof(CanAct), nameof(ActionText), nameof(StatusText),
                     nameof(StatusBrush), nameof(LastEventText), nameof(HasLastEventText) })
            Raise(p);
    }
}
