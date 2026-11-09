using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DevStarter.Services;

namespace DevStarter.Views;

// Aranabilir, gruplu seçim penceresi (Wsidebar AppPickerWindow'dan uyarlandı)
public partial class PickerWindow : Window
{
    readonly Func<Task<List<PickerItem>>> loader;
    readonly Func<Size, Point>? positioner;
    readonly DispatcherTimer filterTimer;
    List<PickerItem> all = new();
    bool dataLoaded;
    bool closing;

    public PickerWindow(string placeholder, Func<Task<List<PickerItem>>> loader, Func<Size, Point>? positioner = null)
    {
        InitializeComponent();
        Placeholder.Text = placeholder;
        this.loader = loader;
        this.positioner = positioner;
        // Her tuşta değil, yazma kısa bir an durunca filtrele
        filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        filterTimer.Tick += (_, _) => FlushFilter();
        Closed += (_, _) => filterTimer.Stop();
        SourceInitialized += (_, _) => Position();
        SizeChanged += (_, _) => Position();
        // Dışarıdan kapatılırken (ör. sahibi kapanırken) Deactivated tekrar Close() çağırmasın
        Closing += (_, _) => closing = true;
        Closed += (_, _) => closing = true;
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        try { all = await loader(); }
        catch (Exception ex) { Log.Error(ex, "picker load"); }
        dataLoaded = true;
        ApplyFilter();
        Position();
    }

    void Position()
    {
        var wa = SystemParameters.WorkArea;
        ItemList.MaxHeight = Math.Min(520, Math.Max(240, wa.Height - 170));
        var size = new Size(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : 600);

        Point p;
        if (positioner != null) p = positioner(size);
        else if (Owner != null && Owner.IsLoaded)
            p = new Point(Owner.Left + (Owner.ActualWidth - size.Width) / 2, Owner.Top + 60);
        else
            p = new Point(wa.Left + (wa.Width - size.Width) / 2, wa.Top + (wa.Height - size.Height) / 2);

        Left = Math.Max(wa.Left, Math.Min(p.X, wa.Right - size.Width));
        Top = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - size.Height));
    }

    void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        List<PickerItem> list;
        if (q.Length == 0)
        {
            list = all;
        }
        else
        {
            var cmp = StringComparison.CurrentCultureIgnoreCase;
            // Başlığı sorguyla başlayan veya grup adı eşleşenler önce; eşitlerde özgün sıra korunur (OrderBy kararlı)
            int Rank(PickerItem a) =>
                a.Title.StartsWith(q, cmp) || a.Group.Contains(q, cmp) ? 0
                : a.Title.Contains(q, cmp) ? 1
                : a.Subtitle.Contains(q, cmp) ? 2
                : -1;
            list = all.Select(a => (Item: a, Rank: Rank(a)))
                      .Where(x => x.Rank >= 0)
                      .OrderBy(x => x.Rank)
                      .Select(x => x.Item)
                      .ToList();
        }

        var view = new ListCollectionView(list);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PickerItem.Group)));
        ItemList.ItemsSource = view;

        int count = list.Count(i => i.Run != null);
        CountText.Text = dataLoaded ? $"{count} seçenek" : "";
        if (list.Count > 0)
        {
            ItemList.SelectedIndex = 0;
            ItemList.ScrollIntoView(ItemList.Items[0]);
            StatusText.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusText.Text = dataLoaded ? "Sonuç yok" : "Yükleniyor...";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!dataLoaded) return;
        filterTimer.Stop();
        filterTimer.Start();
    }

    void FlushFilter()
    {
        if (!filterTimer.IsEnabled) return;
        filterTimer.Stop();
        ApplyFilter();
    }

    void CloseIfOpen()
    {
        if (closing) return;
        closing = true;
        try { Close(); }
        catch (InvalidOperationException) { } // pencere zaten kapanıyor
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseIfOpen();
                e.Handled = true;
                break;
            case Key.Enter:
                FlushFilter();
                if (ItemList.SelectedItem is PickerItem item) Pick(item);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Down:
            {
                FlushFilter();
                int count = ItemList.Items.Count;
                if (count == 0) break;
                int idx = ItemList.SelectedIndex < 0 ? 0 : ItemList.SelectedIndex;
                idx = e.Key == Key.Up ? Math.Max(0, idx - 1) : Math.Min(count - 1, idx + 1);
                ItemList.SelectedIndex = idx;
                ItemList.ScrollIntoView(ItemList.Items[idx]);
                e.Handled = true;
                break;
            }
        }
    }

    void ItemList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        var node = src;
        while (node != null && node is not ListBoxItem)
            node = VisualTreeHelper.GetParent(node);
        if (node is ListBoxItem { Content: PickerItem item }) Pick(item);
    }

    // Önce pencereyi kapat, sonra eylemi çalıştır (eylem dosya iletişim kutusu açabilir)
    void Pick(PickerItem item)
    {
        CloseIfOpen();
        var run = item.Run;
        if (run == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            try { run(); }
            catch (Exception ex) { Log.Error(ex, "picker action"); }
        });
    }

    // WPF kapanırken önce sahibini etkinleştirir; bu da Closing'den önce Deactivated tetikler.
    // Kapatmayı ertelemek, zaten kapanmakta olan pencereye ikinci Close() çağrısını önler.
    void Window_Deactivated(object sender, EventArgs e) => Dispatcher.BeginInvoke(CloseIfOpen);
}
