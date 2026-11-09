using DevStarter.Agents;
using DevStarter.Models;
using DevStarter.Services;

namespace DevStarter.Notifications;

public enum NoticeKind { Waiting, Finished }

public sealed record AgentNotice(AgentSession Session, NoticeKind Kind, string Title, string Body);

// Bildirim hedefi: uygulama içi baloncuk, ileride Telegram vb.
public interface INotifier
{
    Task NotifyAsync(AgentNotice notice);
}

// Ajan durum değişikliklerini kurallara göre bildirime çevirir ve tüm hedeflere dağıtır
public sealed class NotificationHub
{
    static readonly TimeSpan WaitingCooldown = TimeSpan.FromMinutes(1);
    static readonly TimeSpan MinWorkForFinished = TimeSpan.FromSeconds(30);

    readonly List<INotifier> notifiers = new();
    readonly Dictionary<string, DateTime> lastWaiting = new();
    readonly AgentSettings settings;

    public NotificationHub(AgentMonitor monitor, AgentSettings settings)
    {
        this.settings = settings;
        monitor.StateChanged += OnStateChanged;
    }

    public void Add(INotifier notifier) => notifiers.Add(notifier);

    void OnStateChanged(AgentStateChange c)
    {
        var s = c.Session;
        if (c.New == AgentState.Waiting && c.Old != AgentState.Waiting)
        {
            if (!settings.NotifyOnWaiting) return;
            var now = DateTime.UtcNow;
            if (lastWaiting.TryGetValue(s.Key, out var last) && now - last < WaitingCooldown) return;
            lastWaiting[s.Key] = now;
            Publish(new AgentNotice(s, NoticeKind.Waiting, Title(s), s.Detail));
        }
        else if (c.Old == AgentState.Working && c.New == AgentState.Idle)
        {
            if (!settings.NotifyOnFinished) return;
            var worked = DateTime.Now - c.OldSince;
            if (worked < MinWorkForFinished) return;
            var what = s.Title.Length > 0 ? $"\n{s.Title}" : "";
            Publish(new AgentNotice(s, NoticeKind.Finished, Title(s), $"İşini bitirdi ({AgentText.Duration(worked)} sürdü){what}"));
        }
        else if (c.New == AgentState.Ended)
        {
            lastWaiting.Remove(s.Key);
        }
    }

    static string Title(AgentSession s) => $"{s.Label} • {s.KindName}";

    async void Publish(AgentNotice notice)
    {
        foreach (var n in notifiers)
        {
            try { await n.NotifyAsync(notice); }
            catch (Exception ex) { Log.Error(ex, $"notifier {n.GetType().Name}"); }
        }
    }
}

// Kenar çubuğunun bildirim baloncuğu
public sealed class InAppNotifier(MainWindow window) : INotifier
{
    public Task NotifyAsync(AgentNotice notice)
    {
        window.ShowAgentNotice(notice);
        return Task.CompletedTask;
    }
}
