using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevStarter.Discovery;

namespace DevStarter.Agents;

// Hook / eklenti olaylarıyla beslenen oturum tablosu. Olaylar HookServer iş parçacığından gelir,
// Poll() AgentMonitor döngüsünden çağrılır; ikisi de kilit altında.
abstract class HookSource : IAgentSource
{
    static readonly TimeSpan StaleIdle = TimeSpan.FromHours(3);       // bu kadar sessiz kalan boşta oturum gizlenir
    static readonly TimeSpan StaleNoProcess = TimeSpan.FromHours(1);  // süreci bilinmeyen oturumlar için

    protected sealed class Entry
    {
        public string Id = "";
        public string Cwd = "";
        public string Title = "";
        public AgentState State = AgentState.Idle;
        public string WaitingText = "";
        public DateTime Since = DateTime.Now;
        public DateTime Last = DateTime.Now;
        public AgentProcess Proc = AgentProcess.None;
        public DateTime NextTitleLookup;
    }

    protected readonly Dictionary<string, Entry> sessions = new();
    protected readonly object gate = new();

    public abstract AgentKind Kind { get; }
    public DateTime LastEvent { get; private set; }

    protected abstract void Apply(JsonElement e, AgentProcess proc);

    // Kalıcı kayıttan (SQLite vb.) başlık bulmak için; kilit dışında çağrılır
    protected virtual string LookupTitle(string id) => "";

    // Bu olayın oturumu için ajan süreci henüz bilinmiyorsa true
    public virtual bool NeedsProcess(JsonElement e)
    {
        var id = Str(e, "session_id");
        lock (gate) return !sessions.TryGetValue(id, out var s) || s.Proc.Pid == 0;
    }

    public void OnHook(JsonElement e, AgentProcess proc)
    {
        lock (gate)
        {
            LastEvent = DateTime.Now;
            Apply(e, proc);
        }
    }

    public IReadOnlyList<SessionInfo> Poll()
    {
        List<Entry> snapshot;
        lock (gate)
        {
            var now = DateTime.Now;
            foreach (var id in sessions.Keys.ToList())
            {
                var s = sessions[id];
                bool dead = s.Proc.Pid > 0 ? !s.Proc.IsAlive : now - s.Last > StaleNoProcess && s.State != AgentState.Waiting;
                bool stale = s.State == AgentState.Idle && now - s.Last > StaleIdle;
                if (dead || stale) sessions.Remove(id);
            }
            snapshot = sessions.Values.ToList();
        }

        foreach (var s in snapshot.Where(s => s.Title.Length == 0 && DateTime.UtcNow >= s.NextTitleLookup))
        {
            string title = "";
            try { title = LookupTitle(s.Id); } catch { }
            lock (gate)
            {
                if (title.Length > 0 && s.Title.Length == 0) s.Title = TranscriptTail.OneLine(title, 90);
                s.NextTitleLookup = DateTime.UtcNow.AddSeconds(60);
            }
        }

        lock (gate)
        {
            return snapshot.Select(s => new SessionInfo(Kind, s.Id, s.Cwd, s.Title, s.State, s.WaitingText, s.Since, s.Last,
                s.Proc.Pid > 0 ? s.Proc.Pid : null, s.Proc.InVsCode ? "vscode" : "cli")).ToList();
        }
    }

    protected Entry Get(string id, string cwd, AgentProcess proc)
    {
        if (!sessions.TryGetValue(id, out var s))
            sessions[id] = s = new Entry { Id = id };
        if (cwd.Length > 0) s.Cwd = cwd;
        if (proc.Pid > 0) s.Proc = proc;
        s.Last = DateTime.Now;
        return s;
    }

    protected static void SetState(Entry s, AgentState state, string waiting = "")
    {
        if (s.State != state) s.Since = DateTime.Now;
        s.State = state;
        s.WaitingText = state == AgentState.Waiting ? waiting : "";
    }

    protected static string Str(JsonElement e, string name) => TranscriptTail.Str(e, name);

    protected static string PromptTitle(JsonElement e, string field)
    {
        var p = Str(e, field);
        return p.Length > 0 ? TranscriptTail.OneLine(p, 90) : "";
    }

    protected static bool SafeId(string id) => id.Length is > 0 and < 128 && Regex.IsMatch(id, "^[A-Za-z0-9_\\-]+$");
}

// Codex: ~/.codex/hooks.json'a eklenen (async) hook'lar → SessionStart, UserPromptSubmit, PermissionRequest,
// PostToolUse, Stop, Interrupt, SessionEnd
sealed class CodexSource : HookSource
{
    static readonly string StateDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "state_5.sqlite");

    public override AgentKind Kind => AgentKind.Codex;

    protected override void Apply(JsonElement e, AgentProcess proc)
    {
        var id = Str(e, "session_id");
        if (id.Length == 0) return;
        var ev = Str(e, "hook_event_name");
        if (ev == "SessionEnd")
        {
            sessions.Remove(id);
            return;
        }
        var s = Get(id, Str(e, "cwd"), proc);   // SessionStart: yeni oturum boşta olarak eklenir
        switch (ev)
        {
            case "UserPromptSubmit":
                SetState(s, AgentState.Working);
                if (s.Title.Length == 0) s.Title = PromptTitle(e, "prompt");
                break;
            case "PermissionRequest":
                SetState(s, AgentState.Waiting, DescribePermission(e));
                break;
            case "PostToolUse":
                SetState(s, AgentState.Working);
                break;
            case "Stop":
            case "Interrupt":
                SetState(s, AgentState.Idle);
                break;
        }
    }

    static string DescribePermission(JsonElement e)
    {
        var tool = Str(e, "tool_name");
        if (e.TryGetProperty("tool_input", out var input))
        {
            var cmd = Str(input, "command");
            if (cmd.Length > 0) return $"Komut izni: {TranscriptTail.OneLine(cmd, 110)}";
            var desc = Str(input, "description");
            if (desc.Length > 0) return $"İzin: {TranscriptTail.OneLine(desc, 110)}";
        }
        return tool.Length > 0 ? $"{tool} için izin bekliyor" : "İzin vermeni bekliyor";
    }

    protected override string LookupTitle(string id)
    {
        if (!SafeId(id) || !File.Exists(StateDb)) return "";
        var rows = WinSqlite.Query(StateDb, $"SELECT title FROM threads WHERE id = '{id}'", 1);
        return rows.Count > 0 ? rows[0][0] : "";
    }
}

// Gemini CLI: ~/.gemini/settings.json hook'ları → SessionStart, BeforeAgent, Notification(ToolPermission),
// AfterTool, AfterAgent, SessionEnd
sealed class GeminiSource : HookSource
{
    public override AgentKind Kind => AgentKind.Gemini;

    protected override void Apply(JsonElement e, AgentProcess proc)
    {
        var id = Str(e, "session_id");
        if (id.Length == 0) return;
        var ev = Str(e, "hook_event_name");
        if (ev == "SessionEnd")
        {
            sessions.Remove(id);
            return;
        }
        var s = Get(id, Str(e, "cwd"), proc);
        switch (ev)
        {
            case "BeforeAgent":
                SetState(s, AgentState.Working);
                if (s.Title.Length == 0) s.Title = PromptTitle(e, "prompt");
                break;
            case "Notification":
                if (Str(e, "notification_type") == "ToolPermission")
                {
                    var msg = Str(e, "message");
                    SetState(s, AgentState.Waiting, msg.Length > 0 ? $"İzin: {TranscriptTail.OneLine(msg, 110)}" : "İzin vermeni bekliyor");
                }
                break;
            case "AfterTool":
                if (s.State == AgentState.Waiting) SetState(s, AgentState.Working);
                break;
            case "AfterAgent":
                SetState(s, AgentState.Idle);
                break;
        }
    }
}

// OpenCode: ~/.config/opencode/plugins/devstarter-status.js eklentisi → {event, sessionID, status, title, text, directory, pid}
sealed class OpenCodeSource : HookSource
{
    static readonly string Db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode", "opencode.db");

    public override AgentKind Kind => AgentKind.OpenCode;

    // Eklenti kendi pid'ini gönderir; TCP tablosunda aramaya gerek yok
    public override bool NeedsProcess(JsonElement e) => false;

    protected override void Apply(JsonElement e, AgentProcess _)
    {
        var id = Str(e, "sessionID");
        if (id.Length == 0) return;
        var ev = Str(e, "event");
        if (ev == "session.deleted")
        {
            sessions.Remove(id);
            return;
        }

        // Eklenti OpenCode sürecinin içinde çalışır: gönderdiği pid doğrudan ajan sürecidir
        var proc = AgentProcess.None;
        if (e.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pid) && pid > 0)
        {
            long start = 0;
            try
            {
                using var process = Process.GetProcessById(pid);
                start = process.StartTime.ToFileTimeUtc();
            }
            catch { }
            proc = new AgentProcess(pid, start, false);
        }

        var s = Get(id, Str(e, "directory").Replace('/', '\\'), proc);
        var title = Str(e, "title");
        if (title.Length > 0) s.Title = TranscriptTail.OneLine(title, 90);

        switch (ev)
        {
            case "session.status":
                var status = Str(e, "status");
                if (status is "busy" or "retry") { if (s.State != AgentState.Waiting) SetState(s, AgentState.Working); }
                else if (status == "idle") SetState(s, AgentState.Idle);
                break;
            case "session.idle":
            case "session.error":
                SetState(s, AgentState.Idle);
                break;
            case "permission.asked":
                var perm = Str(e, "text");
                SetState(s, AgentState.Waiting, perm.Length > 0 ? $"İzin: {TranscriptTail.OneLine(perm, 110)}" : "İzin vermeni bekliyor");
                break;
            case "question.asked":
                var q = Str(e, "text");
                SetState(s, AgentState.Waiting, q.Length > 0 ? $"Soru: {TranscriptTail.OneLine(q, 140)}" : "Sana soru soruyor");
                break;
            case "permission.replied":
            case "question.replied":
                SetState(s, AgentState.Working);
                break;
        }
    }

    protected override string LookupTitle(string id)
    {
        if (!SafeId(id) || !File.Exists(Db)) return "";
        var rows = WinSqlite.Query(Db, $"SELECT title FROM session WHERE id = '{id}'", 1);
        return rows.Count > 0 ? rows[0][0] : "";
    }
}
