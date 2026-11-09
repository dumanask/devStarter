using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevStarter.Services;

namespace DevStarter.Agents;

// Claude Code her açık oturum için ~/.claude/sessions/<pid>.json tutar:
// {pid, sessionId, cwd, status: busy|shell|idle|waiting, waitingFor, statusUpdatedAt, procStart, name, entrypoint…}
// Ayar değiştirmeye gerek yok; bekleyen sorunun metni transcript'in sonundan okunur.
sealed class ClaudeSource : IAgentSource
{
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    static readonly string SessionsDir = Path.Combine(Root, "sessions");
    static readonly string ProjectsDir = Path.Combine(Root, "projects");
    const int TailBytes = 384 * 1024;

    sealed class Transcript
    {
        public string Path = "";
        public DateTime NextLookup;          // bulunamadıysa tekrar arama zamanı
        public long ScannedLength = -1;
        public DateTime NextTitleScan;
        public string Title = "";
        public long WaitingKey = -1;         // statusUpdatedAt: aynı bekleme için tekrar taranmaz
        public string WaitingText = "";
    }

    readonly Dictionary<string, Transcript> transcripts = new();
    readonly Dictionary<string, SessionInfo> lastGood = new(StringComparer.OrdinalIgnoreCase);
    readonly FileSystemWatcher? watcher;

    public ClaudeSource(Action changed)
    {
        try
        {
            if (!Directory.Exists(SessionsDir)) return;
            watcher = new FileSystemWatcher(SessionsDir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            watcher.Changed += (_, _) => changed();
            watcher.Created += (_, _) => changed();
            watcher.Deleted += (_, _) => changed();
            watcher.Renamed += (_, _) => changed();
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Error(ex, "claude watcher"); }
    }

    public AgentKind Kind => AgentKind.Claude;

    public IReadOnlyList<SessionInfo> Poll()
    {
        var list = new List<SessionInfo>();
        if (!Directory.Exists(SessionsDir)) return list;

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(SessionsDir, "*.json"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out _)) continue;
            files.Add(file);
            var info = Read(file);
            if (info != null) list.Add(info);
        }

        foreach (var gone in lastGood.Keys.Where(k => !files.Contains(k)).ToList()) lastGood.Remove(gone);
        var live = list.Select(i => i.Id).ToHashSet();
        foreach (var gone in transcripts.Keys.Where(k => !live.Contains(k)).ToList()) transcripts.Remove(gone);
        return list;
    }

    SessionInfo? Read(string file)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(ReadShared(file));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Dosya o an yazılıyor olabilir: oturumu düşürmemek için son okunan hâli kullan
            return lastGood.TryGetValue(file, out var prev) && ProcessUtil.IsAlive(prev.Pid ?? 0, null) ? prev : null;
        }

        using (doc)
        {
            var r = doc.RootElement;
            int pid = Int(r, "pid");
            var id = TranscriptTail.Str(r, "sessionId");
            if (pid <= 0 || id.Length == 0) return null;
            if (!ProcessUtil.IsAlive(pid, TranscriptTail.Str(r, "procStart"))) return null;

            var cwd = TranscriptTail.Str(r, "cwd");
            var status = TranscriptTail.Str(r, "status");
            var state = status switch
            {
                "busy" or "shell" => AgentState.Working,
                "waiting" => AgentState.Waiting,
                _ => AgentState.Idle
            };
            long statusAt = Long(r, "statusUpdatedAt");
            long updatedAt = Long(r, "updatedAt");
            long startedAt = Long(r, "startedAt");
            var since = FromMs(statusAt > 0 ? statusAt : updatedAt > 0 ? updatedAt : startedAt);
            var last = FromMs(Math.Max(Math.Max(statusAt, updatedAt), startedAt));

            var t = TranscriptFor(id, cwd);
            var title = TitleFor(t);
            if (title.Length == 0) title = TranscriptTail.Str(r, "name");

            var waiting = "";
            if (state == AgentState.Waiting)
                waiting = WaitingFor(t, statusAt, TranscriptTail.Str(r, "waitingFor"));

            var info = new SessionInfo(AgentKind.Claude, id, cwd, title, state, waiting, since, last, pid,
                TranscriptTail.Str(r, "entrypoint"));
            lastGood[file] = info;
            return info;
        }
    }

    // ---------- transcript ----------

    Transcript TranscriptFor(string sessionId, string cwd)
    {
        if (!transcripts.TryGetValue(sessionId, out var t))
            transcripts[sessionId] = t = new Transcript();
        if (t.Path.Length > 0 && File.Exists(t.Path)) return t;
        if (DateTime.UtcNow < t.NextLookup) return t;

        t.Path = "";
        // Klasör adı: cwd'deki harf/rakam dışı her karakter '-' olur (c:\dev\x → c--dev-x)
        var guess = Path.Combine(ProjectsDir, Regex.Replace(cwd, "[^A-Za-z0-9]", "-"), sessionId + ".jsonl");
        if (File.Exists(guess))
        {
            t.Path = guess;
            return t;
        }
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ProjectsDir))
            {
                var candidate = Path.Combine(dir, sessionId + ".jsonl");
                if (File.Exists(candidate))
                {
                    t.Path = candidate;
                    return t;
                }
            }
        }
        catch { }
        t.NextLookup = DateTime.UtcNow.AddSeconds(60);
        return t;
    }

    static string TitleFor(Transcript t)
    {
        if (t.Path.Length == 0) return t.Title;
        try
        {
            var len = new FileInfo(t.Path).Length;
            if (len == t.ScannedLength || DateTime.UtcNow < t.NextTitleScan) return t.Title;
            t.ScannedLength = len;
            t.NextTitleScan = DateTime.UtcNow.AddSeconds(10);

            string custom = "", ai = "", prompt = "";
            foreach (var line in TranscriptTail.ReadLines(t.Path, TailBytes))
            {
                if (!line.Contains("-title\"") && !line.Contains("\"last-prompt\"")) continue;
                using var doc = TranscriptTail.TryParse(line);
                if (doc == null) continue;
                var r = doc.RootElement;
                switch (TranscriptTail.Str(r, "type"))
                {
                    case "custom-title": custom = TranscriptTail.Str(r, "customTitle"); break;
                    case "ai-title": ai = TranscriptTail.Str(r, "aiTitle"); break;
                    case "last-prompt": prompt = TranscriptTail.Str(r, "lastPrompt"); break;
                }
            }
            var title = custom.Length > 0 ? custom : ai.Length > 0 ? ai : prompt;
            if (title.Length > 0) t.Title = TranscriptTail.OneLine(title, 90);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return t.Title;
    }

    // Yanıtı gelmemiş son tool_use: AskUserQuestion → soru, ExitPlanMode → plan onayı, diğerleri → izin istenen işlem
    static string WaitingFor(Transcript t, long statusAt, string waitingFor)
    {
        if (statusAt == t.WaitingKey && t.WaitingText.Length > 0) return t.WaitingText;
        var text = "";
        if (t.Path.Length > 0)
        {
            try { text = PendingTool(t.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (text.Length == 0)
        {
            text = waitingFor switch
            {
                "permission prompt" => "İzin vermeni bekliyor",
                "input needed" => "Yanıtını bekliyor",
                "dialog open" => "Açık bir pencere var",
                "" => "Yanıtını bekliyor",
                _ => waitingFor
            };
        }
        t.WaitingKey = statusAt;
        t.WaitingText = text;
        return text;
    }

    static string PendingTool(string path)
    {
        var pending = new List<(string Id, string Name, JsonElement Input)>();
        var docs = new List<JsonDocument>();
        try
        {
            foreach (var line in TranscriptTail.ReadLines(path, TailBytes))
            {
                if (!line.Contains("\"tool_use\"") && !line.Contains("\"tool_result\"")) continue;
                var doc = TranscriptTail.TryParse(line);
                if (doc == null) continue;
                docs.Add(doc);
                var r = doc.RootElement;
                if (r.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue;
                if (!r.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array) continue;

                foreach (var block in content.EnumerateArray())
                {
                    switch (TranscriptTail.Str(block, "type"))
                    {
                        case "tool_use":
                            pending.Add((TranscriptTail.Str(block, "id"), TranscriptTail.Str(block, "name"),
                                block.TryGetProperty("input", out var input) ? input : default));
                            break;
                        case "tool_result":
                            var done = TranscriptTail.Str(block, "tool_use_id");
                            pending.RemoveAll(p => p.Id == done);
                            break;
                    }
                }
            }
            if (pending.Count == 0) return "";
            var (_, name, inp) = pending[^1];
            return Describe(name, inp);
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
    }

    static string Describe(string tool, JsonElement input)
    {
        switch (tool)
        {
            case "AskUserQuestion":
                if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("questions", out var qs)
                    && qs.ValueKind == JsonValueKind.Array && qs.GetArrayLength() > 0)
                {
                    var q = TranscriptTail.OneLine(TranscriptTail.Str(qs[0], "question"), 140);
                    int more = qs.GetArrayLength() - 1;
                    return more > 0 ? $"Soru: {q} (+{more} soru)" : $"Soru: {q}";
                }
                return "Sana soru soruyor";
            case "ExitPlanMode":
                return "Planı onaylamanı bekliyor";
            case "Bash":
            case "PowerShell":
                var cmd = TranscriptTail.Str(input, "command");
                return cmd.Length > 0 ? $"Komut izni: {TranscriptTail.OneLine(cmd, 110)}" : "Komut çalıştırmak için izin bekliyor";
            case "Edit":
            case "Write":
            case "MultiEdit":
            case "NotebookEdit":
                var file = TranscriptTail.Str(input, "file_path");
                if (file.Length == 0) file = TranscriptTail.Str(input, "notebook_path");
                return file.Length > 0 ? $"Dosya izni: {Path.GetFileName(file)}" : "Dosya değiştirmek için izin bekliyor";
            case "WebFetch":
                var url = TranscriptTail.Str(input, "url");
                return Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"Web izni: {u.Host}" : "Web erişimi için izin bekliyor";
            default:
                return tool.Length > 0 ? $"{tool} için izin bekliyor" : "";
        }
    }

    // ---------- yardımcılar ----------

    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    static long Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    static DateTime FromMs(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : DateTime.Now;
}
