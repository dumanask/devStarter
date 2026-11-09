using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using DevStarter.Discovery;

namespace DevStarter.Usage;

// OpenCode: yerel veritabanından (opencode.db, salt okunur) maliyet ve token toplamları;
// OpenCode Go aboneliği varsa opencode.ai'den 5 saatlik / haftalık / aylık kullanım.
// Go isteği yalnızca panel açılınca veya "Yenile" ile atılır.
sealed class OpenCodeUsage : IUsageProvider
{
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode");

    public string Name => "OpenCode";
    public bool IncludeNetwork { get; set; }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken token)
    {
        var snap = new UsageSnapshot { Provider = Name, Source = "OpenCode yerel kayıtları" };
        if (!Directory.Exists(DataDir))
        {
            snap.Error = "OpenCode bulunamadı.";
            return snap;
        }

        AddLocalTotals(snap);
        if (IncludeNetwork) await AddGoUsage(snap, token);
        return snap;
    }

    static void AddLocalTotals(UsageSnapshot snap)
    {
        var db = Path.Combine(DataDir, "opencode.db");
        if (!File.Exists(db)) return;
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Date, now.Offset).ToUnixTimeMilliseconds();
        var week = now.AddDays(-7).ToUnixTimeMilliseconds();
        string Line(string label, long since)
        {
            var rows = WinSqlite.Query(db,
                $"SELECT COUNT(*), IFNULL(SUM(cost),0), IFNULL(SUM(IFNULL(tokens_input,0)+IFNULL(tokens_output,0)+IFNULL(tokens_reasoning,0)),0) FROM session WHERE time_updated >= {since}", 3);
            if (rows.Count == 0) return "";
            var r = rows[0];
            double.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cost);
            double.TryParse(r[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var tokens);
            return $"{label}: {r[0]} oturum · ${cost:0.00} · {Tokens(tokens)} token";
        }
        var t = Line("Bugün", today);
        var w = Line("Son 7 gün", week);
        if (t.Length > 0) snap.Notes.Add(t);
        if (w.Length > 0) snap.Notes.Add(w);
    }

    static string Tokens(double n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000:0.#}M",
        >= 1_000 => $"{n / 1_000:0.#}K",
        _ => $"{n:0}"
    };

    static async Task AddGoUsage(UsageSnapshot snap, CancellationToken token)
    {
        var key = GoKey();
        if (key == null) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://opencode.ai/zen/go/v1/usage");
            req.Headers.Add("Authorization", $"Bearer {key}");
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("User-Agent", "devStarter");
            using var res = await http.SendAsync(req, token);
            var text = await res.Content.ReadAsStringAsync(token);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                snap.Notes.Add(text.Contains("subscription", StringComparison.OrdinalIgnoreCase)
                    ? "OpenCode Go aboneliği etkin değil."
                    : "OpenCode Go anahtarı geçersiz.");
                return;
            }
            if (!res.IsSuccessStatusCode)
            {
                snap.Notes.Add($"OpenCode Go kullanımı alınamadı (HTTP {(int)res.StatusCode}).");
                return;
            }

            using var doc = JsonDocument.Parse(text);
            var usage = Json.Prop(doc.RootElement, "usage") ?? doc.RootElement;
            snap.Plan = "Go";
            snap.Source = "opencode.ai + yerel kayıtlar";
            AddWindow(snap, usage, "rolling", "5 saatlik limit");
            AddWindow(snap, usage, "weekly", "Haftalık limit");
            AddWindow(snap, usage, "monthly", "Aylık limit");
            var renews = Json.Time(usage, "renewsAt", "renewAt", "renews_at") ?? Json.Time(doc.RootElement, "renewsAt", "renewAt", "renews_at");
            if (renews is DateTime r) snap.Notes.Add($"Abonelik yenilenme: {r.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("tr-TR"))}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            snap.Notes.Add("OpenCode Go kullanımına ulaşılamadı.");
        }
    }

    static void AddWindow(UsageSnapshot snap, JsonElement usage, string key, string label)
    {
        if (Json.Prop(usage, key) is not JsonElement w) return;
        var percent = Json.Num(w, "usagePercent", "percent", "usedPercent", "used_percent");
        if (percent == null && Json.Num(w, "used", "usage", "usedMicroCents") is double used
            && Json.Num(w, "limit", "total", "limitMicroCents") is double limit && limit > 0)
            percent = used / limit * 100;
        DateTime? reset = Json.Time(w, "resetAt", "resetsAt", "reset_at", "resets_at");
        if (reset == null && Json.Num(w, "resetInSec", "resetIn", "resetsIn", "reset_in") is double secs)
            reset = DateTime.UtcNow.AddSeconds(secs);
        snap.Meters.Add(new UsageMeter { Label = label, UsedPercent = Math.Clamp(percent ?? 0, 0, 100), ResetsAt = reset });
    }

    // auth.json → "opencode-go": {type: "api", key}. Anahtar yalnızca bu istekte kullanılır, saklanmaz.
    static string? GoKey()
    {
        try
        {
            var path = Path.Combine(DataDir, "auth.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (Json.Prop(doc.RootElement, "opencode-go") is JsonElement go && Json.Str(go, "key") is var k && k.Length > 0) return k;
        }
        catch { }
        return null;
    }
}
