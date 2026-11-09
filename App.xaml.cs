using System.Windows;
using DevStarter.Agents;
using DevStarter.Models;
using DevStarter.Notifications;
using DevStarter.Services;

namespace DevStarter;

public partial class App : Application
{
    const string ShowEventName = "Local\\DevStarter.Show";

    Mutex? mutex;
    EventWaitHandle? showEvent;
    RegisteredWaitHandle? showWait;

    public static AppSettings Settings { get; private set; } = new();
    public static NotificationHub? Notifications { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        mutex = new Mutex(true, "Local\\DevStarter.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Zaten çalışıyor (ör. kısayola tekrar tıklandı): açık kopyaya kenar çubuğunu göstermesini söyle
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
                    using (ev) ev.Set();
            }
            catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "dispatcher");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.ExceptionObject as Exception, "domain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "task");
            args.SetObserved();
        };

        base.OnStartup(e);

        EnvironmentCleaner.Clean();
        Settings = SettingsStore.Load();
        ToolLocator.Overrides = Settings.ToolPaths;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Ajan oturumlarını izle; durum değişince bildirim katmanına (şimdilik uygulama içi baloncuk) aktar
        AgentMonitor.Instance.Start(Settings.Projects, Dispatcher, Settings.Agents);
        Notifications = new NotificationHub(AgentMonitor.Instance, Settings.Agents);
        Notifications.Add(new InAppNotifier(window));

        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => Dispatcher.BeginInvoke(window.Reveal), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AgentMonitor.Instance.Stop();
        showWait?.Unregister(null);
        showEvent?.Dispose();
        mutex?.Dispose();
        base.OnExit(e);
    }
}
