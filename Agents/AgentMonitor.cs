using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using DevStarter.Discovery;
using DevStarter.Models;
using DevStarter.Services;

namespace DevStarter.Agents;

interface IAgentSource
{
    AgentKind Kind { get; }
    // Arka plan iş parçacığında çağrılır; o an açık olan oturumları döndürür
    IReadOnlyList<SessionInfo> Poll();
}

// Tüm ajan kaynaklarını birleştirir, oturumları projelerle eşler ve durum değişikliklerini yayınlar.
// Sessions ve olaylar UI iş parçacığındadır.
public sealed class AgentMonitor : ObservableObject
{
    public static AgentMonitor Instance { get; } = new();

    public ObservableCollection<AgentSession> Sessions { get; } = new();
    public event Action<AgentStateChange>? StateChanged;

    readonly Dictionary<string, AgentSession> map = new();
    readonly List<IAgentSource> sources = new();
    readonly HashSet<string> loggedErrors = new();
    readonly SemaphoreSlim wake = new(0, 1);
    readonly Dictionary<string, (string? Dir, DateTime CheckedAt)> dirCache = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? cts;
    Dispatcher? dispatcher;
    IList<Project> projects = Array.Empty<Project>();
    bool initialDone;
    int working, waiting, idle;

    public int WorkingCount { get => working; private set => Set(ref working, value); }
    public int WaitingCount { get => waiting; private set => Set(ref waiting, value); }
    public int IdleCount { get => idle; private set => Set(ref idle, value); }
    public int OpenCount => working + waiting + idle;

    readonly CodexSource codex = new();
    readonly GeminiSource gemini = new();
    readonly OpenCodeSource openCode = new();

    // Codex / Gemini / OpenCode hook'larının durum bildirdiği yerel uç nokta
    public HookServer Hooks { get; } = new();

    public void Start(IList<Project> projectList, Dispatcher uiDispatcher, AgentSettings agentSettings)
    {
        if (cts != null) return;
        projects = projectList;
        dispatcher = uiDispatcher;
        sources.Add(new ClaudeSource(RequestPoll));
        sources.Add(codex);
        sources.Add(gemini);
        sources.Add(openCode);

        Hooks.Received += OnHook;
        Hooks.NeedsProcess = (agent, payload) => SourceFor(agent)?.NeedsProcess(payload) ?? false;
        Hooks.Start(agentSettings.HookPort);
        if (Hooks.Running) AgentIntegrations.RefreshScripts(agentSettings.HookPort);

        cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(() => Loop(token));
    }

    public void Stop()
    {
        cts?.Cancel();
        Hooks.Stop();
    }

    HookSource? SourceFor(string agent) => agent switch
    {
        "codex" => codex,
        "gemini" => gemini,
        "opencode" => openCode,
        _ => null
    };

    void OnHook(string agent, System.Text.Json.JsonElement payload, AgentProcess proc)
    {
        var source = SourceFor(agent);
        if (source == null) return;
        source.OnHook(payload, proc);
        RequestPoll();
    }

    // Bağlantılar sekmesi için: bu ajandan en son ne zaman olay geldi
    public DateTime? LastHookEvent(AgentKind kind)
    {
        var t = kind switch
        {
            AgentKind.Codex => codex.LastEvent,
            AgentKind.Gemini => gemini.LastEvent,
            AgentKind.OpenCode => openCode.LastEvent,
            _ => default
        };
        return t == default ? null : t;
    }

    // Bir kaynak değişiklik fark ettiğinde (dosya izleyici, hook) beklemeden yeniden okur
    public void RequestPoll()
    {
        try { if (wake.CurrentCount == 0) wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var list = Collect();
            try { await dispatcher!.InvokeAsync(() => Merge(list), DispatcherPriority.Background, token); }
            catch (OperationCanceledException) { return; }
            try
            {
                await wake.WaitAsync(TimeSpan.FromSeconds(2), token);
                await Task.Delay(150, token);   // ardışık dosya olaylarını tek okumada topla
            }
            catch (OperationCanceledException) { return; }
        }
    }

    List<SessionInfo> Collect()
    {
        var all = new List<SessionInfo>();
        foreach (var source in sources)
        {
            try { all.AddRange(source.Poll()); }
            catch (Exception ex)
            {
                // Aynı hatayı her 2 sn'de bir kaydetme
                if (loggedErrors.Add($"{source.Kind}:{ex.GetType().Name}:{ex.Message}")) Log.Error(ex, $"agent source {source.Kind}");
            }
        }
        return all;
    }

    void Merge(List<SessionInfo> list)
    {
        var changes = new List<AgentStateChange>();
        var seen = new HashSet<string>();
        foreach (var info in list)
        {
            var key = $"{info.Kind}:{info.Id}";
            if (!seen.Add(key)) continue;
            if (map.TryGetValue(key, out var s))
            {
                var old = s.State;
                var oldSince = s.Since;
                s.Apply(info);
                if (old != s.State) changes.Add(new AgentStateChange(s, old, s.State, oldSince, false));
            }
            else
            {
                s = new AgentSession(info);
                map[key] = s;
                Sessions.Add(s);
                if (initialDone) changes.Add(new AgentStateChange(s, AgentState.Ended, s.State, s.Since, true));
            }
        }

        foreach (var key in map.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var s = map[key];
            map.Remove(key);
            var old = s.State;
            var oldSince = s.Since;
            s.State = AgentState.Ended;
            Sessions.Remove(s);
            changes.Add(new AgentStateChange(s, old, AgentState.Ended, oldSince, false));
        }

        Remap();
        initialDone = true;
        foreach (var c in changes)
        {
            try { StateChanged?.Invoke(c); }
            catch (Exception ex) { Log.Error(ex, "agent state changed"); }
        }
    }

    // Proje listesi değişince (ekleme, düzenleme) çağrılır
    public void Remap()
    {
        var dirs = new List<(string Dir, Project Project)>();
        foreach (var p in projects)
            foreach (var t in p.Targets)
                if (DirOf(t) is string d) dirs.Add((d, p));

        var counts = new Dictionary<Project, (int W, int Wa, int I, HashSet<AgentKind> Kinds)>();
        int w = 0, wa = 0, i = 0;
        foreach (var s in Sessions)
        {
            s.Project = Match(s.Cwd, dirs);
            switch (s.State)
            {
                case AgentState.Working: w++; break;
                case AgentState.Waiting: wa++; break;
                case AgentState.Idle: i++; break;
            }
            if (s.Project == null) continue;
            counts.TryGetValue(s.Project, out var c);
            c.Kinds ??= new HashSet<AgentKind>();
            c.Kinds.Add(s.Kind);
            switch (s.State)
            {
                case AgentState.Working: c.W++; break;
                case AgentState.Waiting: c.Wa++; break;
                case AgentState.Idle: c.I++; break;
            }
            counts[s.Project] = c;
        }

        foreach (var p in projects)
        {
            if (counts.TryGetValue(p, out var c))
                p.SetAgentCounts(c.W, c.Wa, c.I, string.Join(", ", c.Kinds.Select(AgentText.KindName)));
            else
                p.SetAgentCounts(0, 0, 0, "");
        }

        WorkingCount = w;
        WaitingCount = wa;
        IdleCount = i;
        Raise(nameof(OpenCount));
    }

    static Project? Match(string cwd, List<(string Dir, Project Project)> dirs)
    {
        var c = NormalizeDir(cwd);
        if (c.Length == 0) return null;
        Project? best = null;
        int bestLen = -1;
        foreach (var (dir, p) in dirs)
        {
            bool hit = c.Equals(dir, StringComparison.OrdinalIgnoreCase)
                       || (c.Length > dir.Length && c.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                           && c[dir.Length] == Path.DirectorySeparatorChar);
            if (hit && dir.Length > bestLen)
            {
                best = p;
                bestLen = dir.Length;
            }
        }
        return best;
    }

    // Hedefin klasörü (dosyaysa bulunduğu klasör). Disk kontrolleri 30 sn önbellekte tutulur.
    string? DirOf(LaunchTarget t)
    {
        if (t.Kind is TargetKind.Url or TargetKind.Chrome or TargetKind.Custom) return null;
        var path = t.Path;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.StartsWith(@"\\")) return null;
        if (dirCache.TryGetValue(path, out var cached) && DateTime.UtcNow - cached.CheckedAt < TimeSpan.FromSeconds(30))
            return cached.Dir;

        string? dir = null;
        try
        {
            if (Directory.Exists(path)) dir = path;
            else if (File.Exists(path)) dir = Path.GetDirectoryName(path);
        }
        catch { }
        if (dir != null) dir = NormalizeDir(dir);
        dirCache[path] = (dir, DateTime.UtcNow);
        return dir;
    }

    public static string NormalizeDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (path.StartsWith(@"\\?\")) path = path[4..];
        return Suggestions.Norm(path.Replace('/', '\\'));
    }
}
