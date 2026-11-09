using System.IO;
using System.Windows.Media;
using DevStarter.Models;

namespace DevStarter.Agents;

public enum AgentKind { Claude, Codex, Gemini, OpenCode }

// Working: ajan çalışıyor · Waiting: izin/yanıt bekliyor (sarı) · Idle: işini bitirdi, sıra kullanıcıda
public enum AgentState { Working, Waiting, Idle, Ended }

// Bir kaynaktan okunan anlık oturum bilgisi (arka plan iş parçacığında üretilir)
public sealed record SessionInfo(
    AgentKind Kind,
    string Id,
    string Cwd,
    string Title,
    AgentState State,
    string WaitingText,
    DateTime Since,
    DateTime LastActivity,
    int? Pid,
    string Entrypoint);

public sealed record AgentStateChange(AgentSession Session, AgentState Old, AgentState New, DateTime OldSince, bool IsNew);

public static class AgentColors
{
    public static readonly SolidColorBrush Working = Make(0x22, 0xC5, 0x5E);
    public static readonly SolidColorBrush Waiting = Make(0xFA, 0xCC, 0x15);
    public static readonly SolidColorBrush Idle = Make(0x9A, 0x9A, 0xA4);
    public static readonly SolidColorBrush Ended = Make(0x55, 0x55, 0x5C);

    public static SolidColorBrush For(AgentState s) => s switch
    {
        AgentState.Working => Working,
        AgentState.Waiting => Waiting,
        AgentState.Idle => Idle,
        _ => Ended
    };

    public static SolidColorBrush Kind(AgentKind k) => k switch
    {
        AgentKind.Claude => ClaudeBrush,
        AgentKind.Codex => CodexBrush,
        AgentKind.Gemini => GeminiBrush,
        _ => OpenCodeBrush
    };

    static readonly SolidColorBrush ClaudeBrush = Make(0xD9, 0x77, 0x57);
    static readonly SolidColorBrush CodexBrush = Make(0x2B, 0x2D, 0x31);
    static readonly SolidColorBrush GeminiBrush = Make(0x3B, 0x7B, 0xF0);
    static readonly SolidColorBrush OpenCodeBrush = Make(0x4B, 0x4F, 0x5A);

    static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public static class AgentText
{
    public static string KindName(AgentKind k) => k switch
    {
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        AgentKind.Gemini => "Gemini",
        _ => "OpenCode"
    };

    public static string KindGlyph(AgentKind k) => k switch
    {
        AgentKind.Claude => "✻",
        AgentKind.Codex => ">_",
        AgentKind.Gemini => "✦",
        _ => "OC"
    };

    public static string StateName(AgentState s) => s switch
    {
        AgentState.Working => "Çalışıyor",
        AgentState.Waiting => "Seni bekliyor",
        AgentState.Idle => "Boşta",
        _ => "Kapandı"
    };

    // "45 sn", "5 dk", "2 sa 10 dk", "3 gün"
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return $"{Math.Max(1, (int)t.TotalSeconds)} sn";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} dk";
        if (t.TotalDays < 1) return t.Minutes > 0 ? $"{(int)t.TotalHours} sa {t.Minutes} dk" : $"{(int)t.TotalHours} sa";
        return t.Hours > 0 && t.TotalDays < 7 ? $"{(int)t.TotalDays} gün {t.Hours} sa" : $"{(int)t.TotalDays} gün";
    }
}

public sealed class AgentSession : ObservableObject
{
    string cwd = "";
    string title = "";
    string waitingText = "";
    string entrypoint = "";
    AgentState state;
    DateTime since;
    DateTime lastActivity;
    int? pid;
    Project? project;

    public AgentSession(SessionInfo info)
    {
        Kind = info.Kind;
        Id = info.Id;
        Apply(info);
    }

    public AgentKind Kind { get; }
    public string Id { get; }
    public string Key => $"{Kind}:{Id}";

    public string Cwd { get => cwd; private set { if (Set(ref cwd, value)) { Raise(nameof(FolderName)); Raise(nameof(Location)); } } }
    public string Title { get => title; private set { if (Set(ref title, value)) Raise(nameof(DisplayTitle)); } }
    public string WaitingText { get => waitingText; private set { if (Set(ref waitingText, value)) Raise(nameof(Detail)); } }
    public string Entrypoint { get => entrypoint; private set => Set(ref entrypoint, value); }
    public int? Pid { get => pid; private set => Set(ref pid, value); }
    public DateTime LastActivity { get => lastActivity; private set => Set(ref lastActivity, value); }

    public AgentState State
    {
        get => state;
        set
        {
            if (!Set(ref state, value)) return;
            Raise(nameof(StateText));
            Raise(nameof(StateBrush));
            Raise(nameof(IsWaiting));
            Raise(nameof(StateOrder));
            Raise(nameof(Detail));
        }
    }

    public DateTime Since { get => since; private set { if (Set(ref since, value)) Raise(nameof(Detail)); } }

    public Project? Project
    {
        get => project;
        set
        {
            if (!Set(ref project, value)) return;
            Raise(nameof(GroupName));
            Raise(nameof(GroupOrder));
        }
    }

    public string KindName => AgentText.KindName(Kind);
    public string KindGlyph => AgentText.KindGlyph(Kind);
    public Brush KindBrush => AgentColors.Kind(Kind);
    public string StateText => AgentText.StateName(state);
    public Brush StateBrush => AgentColors.For(state);
    public bool IsWaiting => state == AgentState.Waiting;
    public int StateOrder => state switch { AgentState.Waiting => 0, AgentState.Working => 1, _ => 2 };

    public string FolderName
    {
        get
        {
            var trimmed = Path.TrimEndingDirectorySeparator(cwd);
            var name = Path.GetFileName(trimmed);
            return name.Length > 0 ? name : trimmed;
        }
    }

    public string DisplayTitle => title.Length > 0 ? title : FolderName;
    public string Location => $"{KindName} · {cwd}";
    public string GroupName => project?.Name ?? "Diğer klasörler";
    public string GroupOrder => project != null ? "0" + project.Name : "1";
    public string Label => project?.Name ?? FolderName;

    // Satırın ikinci satırı: bekliyorsa ne beklediği, değilse ne kadar süredir o durumda olduğu
    public string Detail
    {
        get
        {
            var elapsed = AgentText.Duration(DateTime.Now - since);
            return state switch
            {
                AgentState.Waiting => waitingText.Length > 0 ? waitingText : "Yanıtını bekliyor",
                AgentState.Working => $"{elapsed} süredir çalışıyor",
                AgentState.Idle => $"Sıra sende · {elapsed}",
                _ => "Kapandı"
            };
        }
    }

    public void Apply(SessionInfo info)
    {
        Cwd = info.Cwd;
        Title = info.Title;
        WaitingText = info.WaitingText;
        Entrypoint = info.Entrypoint;
        Pid = info.Pid;
        LastActivity = info.LastActivity;
        Since = info.Since;
        State = info.State;
    }

    // Geçen süre metnini tazelemek için panelden periyodik çağrılır
    public void Tick() => Raise(nameof(Detail));
}
