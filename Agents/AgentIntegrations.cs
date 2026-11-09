using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevStarter.Services;

namespace DevStarter.Agents;

public sealed record IntegrationStatus(AgentKind Kind, bool AppFound, bool Installed, string Detail);

// Codex / Gemini / OpenCode'a devStarter'a durum bildiren hook'ları (eklentiyi) kurar ve kaldırır.
// Değiştirilen her ayar dosyası önce %APPDATA%\devStarter\backups altına yedeklenir.
// devStarter'ın eklediği girdiler komut yolundaki "devStarter/hooks/" işaretinden tanınır; başka hiçbir şeye dokunulmaz.
public static class AgentIntegrations
{
    // Testlerde geçici bir klasöre yönlendirilebilir
    internal static string Home { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string CodexDir => Path.Combine(Home, ".codex");
    static string CodexHooks => Path.Combine(CodexDir, "hooks.json");
    static string GeminiDir => Path.Combine(Home, ".gemini");
    static string GeminiSettings => Path.Combine(GeminiDir, "settings.json");
    static string OpenCodeConfig => Path.Combine(Home, ".config", "opencode");
    static string OpenCodePlugin => Path.Combine(OpenCodeConfig, "plugins", "devstarter-status.js");
    static readonly string BackupDir = Path.Combine(Log.Dir, "backups");
    const string Marker = "devstarter/hooks/";

    static readonly string[] CodexEvents = { "SessionStart", "UserPromptSubmit", "PermissionRequest", "PostToolUse", "Stop", "Interrupt", "SessionEnd" };
    static readonly string[] GeminiEvents = { "SessionStart", "BeforeAgent", "Notification", "AfterTool", "AfterAgent", "SessionEnd" };

    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static IntegrationStatus Status(AgentKind kind)
    {
        try
        {
            switch (kind)
            {
                case AgentKind.Claude:
                    bool claude = Directory.Exists(Path.Combine(Home, ".claude"));
                    return new(kind, claude, claude, claude ? "Ek ayar gerekmez: oturumlar Claude Code'un kendi kayıtlarından okunur." : "Claude Code bulunamadı.");
                case AgentKind.Codex:
                    bool codex = Directory.Exists(CodexDir);
                    bool codexOn = codex && File.Exists(CodexHooks) && ContainsMarker(File.ReadAllText(CodexHooks));
                    return new(kind, codex, codexOn, !codex ? "Codex bulunamadı (~/.codex yok)."
                        : codexOn ? "Hook'lar kurulu (~/.codex/hooks.json). Codex yeni hook'ları ancak sen onayladıktan sonra çalıştırır: Codex'te /hooks yaz ve devStarter hook'larını onayla."
                        : "Kurulunca Codex oturumlarının çalışıyor / izin bekliyor / bitti durumları görünür.");
                case AgentKind.Gemini:
                    bool gemini = Directory.Exists(GeminiDir);
                    bool geminiOn = gemini && File.Exists(GeminiSettings) && ContainsMarker(File.ReadAllText(GeminiSettings));
                    return new(kind, gemini, geminiOn, !gemini ? "Gemini CLI bulunamadı (~/.gemini yok)."
                        : geminiOn ? "Hook'lar kurulu (~/.gemini/settings.json)."
                        : "Kurulunca Gemini CLI oturumlarının durumu görünür.");
                default:
                    bool oc = Directory.Exists(OpenCodeConfig) || Directory.Exists(Path.Combine(Home, ".local", "share", "opencode"));
                    bool ocOn = File.Exists(OpenCodePlugin);
                    return new(kind, oc, ocOn, !oc ? "OpenCode bulunamadı."
                        : ocOn ? "Eklenti kurulu (~/.config/opencode/plugins/devstarter-status.js). Açık OpenCode pencereleri yeniden başlatılınca devreye girer."
                        : "Kurulunca OpenCode oturumlarının durumu ve soruları görünür.");
            }
        }
        catch (Exception ex)
        {
            return new(kind, true, false, $"Ayar dosyası okunamadı: {ex.Message}");
        }
    }

    public static void Install(AgentKind kind, int port)
    {
        WriteScripts(port);
        switch (kind)
        {
            case AgentKind.Codex:
                EditJson(CodexHooks, "codex", root =>
                {
                    var hooks = Obj(root, "hooks");
                    foreach (var ev in CodexEvents)
                    {
                        var list = Arr(hooks, ev);
                        RemoveOurs(list);
                        // async: Codex beklemeden devam eder; SessionEnd/Interrupt için üst sınır 3 sn
                        list.Add(new JsonObject
                        {
                            ["hooks"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = ScriptCommand("codex"),
                                ["timeout"] = ev is "SessionEnd" or "Interrupt" ? 3 : 5,
                                ["async"] = true
                            })
                        });
                    }
                });
                break;
            case AgentKind.Gemini:
                EditJson(GeminiSettings, "gemini", root =>
                {
                    var hooks = Obj(root, "hooks");
                    foreach (var ev in GeminiEvents)
                    {
                        var list = Arr(hooks, ev);
                        RemoveOurs(list);
                        // Notification türü (ToolPermission) devStarter tarafında süzülür
                        list.Add(new JsonObject
                        {
                            ["hooks"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = ScriptCommand("gemini"),
                                ["timeout"] = 5000
                            })
                        });
                    }
                });
                break;
            case AgentKind.OpenCode:
                Directory.CreateDirectory(Path.GetDirectoryName(OpenCodePlugin)!);
                File.WriteAllText(OpenCodePlugin, PluginSource(port), new UTF8Encoding(false));
                break;
        }
    }

    public static void Uninstall(AgentKind kind)
    {
        switch (kind)
        {
            case AgentKind.Codex:
                if (File.Exists(CodexHooks)) EditJson(CodexHooks, "codex", root => RemoveAll(root));
                break;
            case AgentKind.Gemini:
                if (File.Exists(GeminiSettings)) EditJson(GeminiSettings, "gemini", root => RemoveAll(root));
                break;
            case AgentKind.OpenCode:
                if (File.Exists(OpenCodePlugin))
                {
                    Backup(OpenCodePlugin, "opencode");
                    File.Delete(OpenCodePlugin);
                }
                break;
        }
    }

    // Port değişirse veya betikler silindiyse kurulu entegrasyonların betiklerini yeniden yazar
    public static void RefreshScripts(int port)
    {
        try
        {
            if (Status(AgentKind.Codex).Installed || Status(AgentKind.Gemini).Installed) WriteScripts(port);
            if (File.Exists(OpenCodePlugin))
            {
                var current = File.ReadAllText(OpenCodePlugin);
                var wanted = PluginSource(port);
                if (current != wanted) File.WriteAllText(OpenCodePlugin, wanted, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) { Log.Error(ex, "refresh hook scripts"); }
    }

    // ---------- hook betikleri ----------

    static void WriteScripts(int port)
    {
        Directory.CreateDirectory(HookServer.HooksDir);
        foreach (var agent in new[] { "codex", "gemini" })
        {
            var path = Path.Combine(HookServer.HooksDir, $"{agent}-hook.cmd");
            var text = Script(agent, port);
            if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text, Encoding.ASCII);
        }
    }

    // devStarter kapalıyken kilit dosyası silinebilir → bağlanmaya çalışmadan çık (stdin yine de okunur).
    // Hiçbir şey yazdırmaz ve hep 0 ile çıkar: ajanın davranışını etkilemez.
    static string Script(string agent, int port) =>
        "@echo off\r\n" +
        $"rem devStarter agent status bridge ({agent}). Does nothing while devStarter is closed.\r\n" +
        "del \"%~dp0devstarter.lock\" >nul 2>&1\r\n" +
        "if not exist \"%~dp0devstarter.lock\" (\r\n" +
        "  more >nul 2>&1\r\n" +
        "  exit /b 0\r\n" +
        ")\r\n" +
        "\"%SystemRoot%\\System32\\curl.exe\" -s -o NUL --connect-timeout 1 -m 3 -X POST -H \"Content-Type: application/json\" " +
        $"--data-binary @- http://127.0.0.1:{port}/hook/{agent} >nul 2>&1\r\n" +
        "exit /b 0\r\n";

    // Ajanın çalıştıracağı komut: boşluk içeren yollar kabuklarda sorun çıkarmasın diye kısa (8.3) yol
    static string ScriptCommand(string agent)
    {
        var path = Path.Combine(HookServer.HooksDir, $"{agent}-hook.cmd");
        if (path.Contains(' '))
        {
            var sb = new StringBuilder(520);
            if (GetShortPathName(path, sb, sb.Capacity) > 0) path = sb.ToString();
        }
        return path.Replace('\\', '/');
    }

    static bool ContainsMarker(string text) => text.Replace('\\', '/').Contains(Marker, StringComparison.OrdinalIgnoreCase)
                                               || text.Contains("DEVSTA~", StringComparison.OrdinalIgnoreCase);

    static bool IsOurs(JsonNode? group)
    {
        if (group?["hooks"] is not JsonArray hooks) return false;
        return hooks.Any(h => h?["command"]?.GetValue<string>() is string c && ContainsMarker(c));
    }

    static void RemoveOurs(JsonArray list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (IsOurs(list[i])) list.RemoveAt(i);
    }

    static void RemoveAll(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var name in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[name] is not JsonArray list) continue;
            RemoveOurs(list);
            if (list.Count == 0) hooks.Remove(name);
        }
        // Gemini'de "hooks" yalnızca bizim için oluşturulduysa kaldır; Codex'te boş nesne kalsın (orijinal biçim)
        if (hooks.Count == 0 && root.Count > 1) root.Remove("hooks");
    }

    // ---------- JSON düzenleme ----------

    // Dosyanın satır sonu (LF/CRLF), BOM ve sondaki yeni satır biçimi korunur: kaldırınca dosya özgün hâline döner
    static void EditJson(string path, string backupName, Action<JsonObject> edit)
    {
        JsonObject root;
        bool bom = false;
        string newline = "\n";
        bool trailingNewline = true;
        if (File.Exists(path))
        {
            Backup(path, backupName);
            var raw = File.ReadAllBytes(path);
            bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            var text = Encoding.UTF8.GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
            if (text.Contains("\r\n")) newline = "\r\n";
            trailingNewline = text.Length == 0 || text.EndsWith('\n');
            root = text.Trim().Length == 0
                ? new JsonObject()
                : JsonNode.Parse(text, null, ReadOptions) as JsonObject ?? throw new InvalidDataException($"{path} bir JSON nesnesi değil.");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            root = new JsonObject();
        }
        edit(root);
        var options = new JsonSerializerOptions(WriteOptions) { NewLine = newline };
        var output = root.ToJsonString(options) + (trailingNewline ? newline : "");
        var tmp = path + ".devstarter.tmp";
        File.WriteAllText(tmp, output, new UTF8Encoding(bom));
        File.Move(tmp, path, overwrite: true);
    }

    static void Backup(string path, string name)
    {
        Directory.CreateDirectory(BackupDir);
        var ext = Path.GetExtension(path);
        File.Copy(path, Path.Combine(BackupDir, $"{name}-{Path.GetFileNameWithoutExtension(path)}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}"), overwrite: true);
    }

    static JsonObject Obj(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject o) return o;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    static JsonArray Arr(JsonObject parent, string name)
    {
        if (parent[name] is JsonArray a) return a;
        var created = new JsonArray();
        parent[name] = created;
        return created;
    }

    // ---------- OpenCode eklentisi ----------

    static string PluginSource(int port)
    {
        var lockJson = JsonSerializer.Serialize(HookServer.LockPath);
        return PluginTemplate.Replace("__PORT__", port.ToString()).Replace("__LOCK__", lockJson);
    }

    const string PluginTemplate = """
// devStarter: OpenCode oturum durumlarını (çalışıyor / boşta / izin veya yanıt bekliyor) devStarter'a bildirir.
// Yalnızca 127.0.0.1'e gönderir; devStarter kapalıyken hiçbir şey yapmaz.
// Kaldırmak için: devStarter → Ajanlar → Bağlantılar → OpenCode → Kaldır (ya da bu dosyayı sil).
import { unlinkSync } from "node:fs";

const PORT = __PORT__;
const LOCK = __LOCK__;
const PERMISSION_SETTLE_MS = 800;

// devStarter çalışırken kilit dosyasını açık tutar; silinebiliyorsa devStarter kapalıdır
function devStarterRunning() {
  try { unlinkSync(LOCK); return false; }
  catch (e) { return !!e && (e.code === "EBUSY" || e.code === "EPERM" || e.code === "EACCES"); }
}

function post(body) {
  if (!devStarterRunning()) return;
  fetch(`http://127.0.0.1:${PORT}/hook/opencode`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ ...body, pid: process.pid }),
    signal: AbortSignal.timeout(2000),
  }).catch(() => {});
}

export const DevStarterStatus = async ({ directory }) => {
  const children = new Set();
  const replied = new Set();
  const send = (event, sessionID, extra = {}) => {
    if (!sessionID || children.has(sessionID)) return;
    post({ event, sessionID, directory, ...extra });
  };
  return {
    event: async ({ event }) => {
      try {
        const type = event?.type || "";
        const p = event?.properties || {};
        switch (type) {
          case "session.created":
          case "session.updated": {
            const info = p.info || {};
            if (info.parentID) { children.add(info.id); return; }
            send(type, info.id, { title: info.title || "", directory: info.directory || directory });
            return;
          }
          case "session.deleted": send(type, p.info?.id); return;
          case "session.status": send(type, p.sessionID, { status: p.status?.type || "" }); return;
          case "session.idle":
          case "session.error": send(type, p.sessionID); return;
          case "permission.asked":
          case "permission.v2.asked": {
            const id = p.id;
            const text = [p.title || p.permission || p.action || p.type || "", [].concat(p.patterns || p.pattern || p.resources || []).join(" ")].join(" ").trim();
            // Kurallarla anında yanıtlanan izinler "bekliyor" sayılmasın
            setTimeout(() => { if (!replied.has(id)) send("permission.asked", p.sessionID, { text }); }, PERMISSION_SETTLE_MS);
            return;
          }
          case "permission.replied":
          case "permission.v2.replied":
            replied.add(p.requestID || p.permissionID || p.id);
            send("permission.replied", p.sessionID);
            return;
          case "question.asked":
          case "question.v2.asked": {
            const q = (p.questions || [])[0] || {};
            send("question.asked", p.sessionID, { text: q.question || q.header || "" });
            return;
          }
          case "question.replied":
          case "question.v2.replied":
          case "question.rejected":
          case "question.v2.rejected":
            send("question.replied", p.sessionID);
            return;
        }
      } catch {}
    },
  };
};
""";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);
}
