using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Threading;

namespace DevStarter.Services;

// Simgeleri arayüz iş parçacığını bloklamadan, tek bir arka plan STA iş parçacığında çıkarır
// (kabuk/COM simge çıkarma STA ister). Sonuç, isteyen Dispatcher'a geri gönderilir.
public static class IconLoader
{
    static readonly BlockingCollection<(string Path, Dispatcher Dispatcher, Action<ImageSource?> Done)> queue = new();

    static IconLoader()
    {
        var worker = new Thread(Run) { IsBackground = true, Name = "devStarter.IconLoader", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static void Request(string path, Action<ImageSource?> done)
        => queue.Add((path, Dispatcher.CurrentDispatcher, done));

    static void Run()
    {
        foreach (var (path, dispatcher, done) in queue.GetConsumingEnumerable())
        {
            ImageSource? icon = null;
            try { icon = IconService.GetIcon(path); }
            catch (Exception ex) { Log.Error(ex, $"icon {path}"); }
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => done(icon));
        }
    }
}
