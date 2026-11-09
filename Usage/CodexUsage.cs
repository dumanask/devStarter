using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using DevStarter.Agents;
using DevStarter.Discovery;
using DevStarter.Services;

namespace DevStarter.Usage;

// Codex: resmî "codex app-server" JSON-RPC (account/rateLimits/read). Model kullanmaz; reset haklarını da verir.
// Codex bulunamazsa / yanıt vermezse son oturum kaydındaki (rollout) rate_limits kullanılır.
sealed class CodexUsage : IUsageProvider
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string Name => "Codex";

    public async Task<UsageSnapshot> FetchAsync(CancellationToken token)
    {
        var exe = FindExe();
        if (exe != null)
        {
            try { return await FromAppServer(exe, token); }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                Log.Error(ex, "codex app-server");
            }
        }
        var fallback = FromRollout();
        if (fallback != null) return fallback;
        return new UsageSnapshot
        {
            Provider = Name,
            Error = exe == null ? "Codex bulunamadı." : "Codex'ten kullanım bilgisi alınamadı."
        };
    }

    // npm ile kurulu native codex.exe, yoksa VS Code eklentisinin en yeni sürümündeki
    static string? FindExe()
    {
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe");
        if (File.Exists(npm)) return npm;
        try
        {
            var ext = Path.Combine(Home, ".vscode", "extensions");
            if (Directory.Exists(ext))
            {
                return Directory.EnumerateDirectories(ext, "openai.chatgpt-*")
                    .Select(d => Path.Combine(d, "bin", "windows-x86_64", "codex.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }
        catch { }
        return null;
    }

    static async Task<UsageSnapshot> FromAppServer(string exe, CancellationToken token)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var a in new[] { "-s", "read-only", "-a", "never", "app-server" }) psi.ArgumentList.Add(a);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("codex başlatılamadı");
        _ = p.StandardError.ReadToEndAsync(timeout.Token);   // tampon dolmasın
        try
        {
            async Task Send(string json)
            {
                await p.StandardInput.WriteLineAsync(json.AsMemory(), timeout.Token);
                await p.StandardInput.FlushAsync(timeout.Token);
            }

            async Task<JsonDocument> WaitFor(int id)
            {
                while (true)
                {
                    var line = await p.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException("codex app-server kapandı");
                    if (!line.StartsWith('{')) continue;
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); }
                    catch (JsonException) { continue; }
                    if (doc.RootElement.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id) return doc;
                    doc.Dispose();
                }
            }

            await Send("""{"id":1,"method":"initialize","params":{"clientInfo":{"name":"devstarter","title":"devStarter","version":"1.0.0"}}}""");
            using (var init = await WaitFor(1))
                if (init.RootElement.TryGetProperty("error", out var err)) throw new InvalidOperationException(err.ToString());
            await Send("""{"method":"initialized"}""");
            await Send("""{"id":2,"method":"account/rateLimits/read"}""");
            using var res = await WaitFor(2);
            if (!res.RootElement.TryGetProperty("result", out var result)) throw new InvalidOperationException(res.RootElement.ToString());
            return Parse(result, camel: true, "Codex (resmî app-server)");
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
        }
    }

    // app-server (camelCase) ve rollout dosyası (snake_case) aynı yapıyı taşır
    static UsageSnapshot Parse(JsonElement result, bool camel, string source)
    {
        var snap = new UsageSnapshot { Provider = "Codex", Source = source };
        var limits = camel ? Json.Prop(result, "rateLimits") ?? result : result;
        snap.Plan = Json.Str(limits, "planType", "plan_type") switch
        {
            "plus" => "Plus",
            "pro" => "Pro",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "free" => "Free",
            var other => other
        };

        AddWindow(snap, limits, "primary");
        AddWindow(snap, limits, "secondary");

        if (Json.Prop(limits, "credits") is JsonElement credits)
        {
            bool unlimited = Json.Prop(credits, "unlimited") is JsonElement u && u.ValueKind == JsonValueKind.True;
            bool has = Json.Prop(credits, "hasCredits", "has_credits") is JsonElement h && h.ValueKind == JsonValueKind.True;
            var balance = Json.Str(credits, "balance");
            if (unlimited) snap.Notes.Add("Kredi: sınırsız");
            else if (has) snap.Notes.Add($"Kredi bakiyesi: {balance}");
        }

        if (Json.Prop(result, "rateLimitResetCredits") is JsonElement resets)
        {
            int count = (int)(Json.Num(resets, "availableCount") ?? 0);
            if (count > 0)
            {
                DateTime? firstExpiry = null;
                string title = "";
                if (Json.Prop(resets, "credits") is JsonElement list && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in list.EnumerateArray())
                    {
                        if (Json.Str(c, "status") is var st && st.Length > 0 && st != "available") continue;
                        var exp = Json.Time(c, "expiresAt");
                        if (exp != null && (firstExpiry == null || exp < firstExpiry)) firstExpiry = exp;
                        if (title.Length == 0) title = Json.Str(c, "title");
                    }
                }
                var what = title.Contains("Weekly", StringComparison.OrdinalIgnoreCase) ? " (her biri haftalık + 5 saatlik limiti sıfırlar)" : "";
                var until = firstExpiry is DateTime d
                    ? $"; ilkinin son kullanma tarihi {d.ToLocalTime().ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))}"
                    : "";
                snap.Notes.Add($"Reset hakkı: {count}{what}{until}");
            }
            else
            {
                snap.Notes.Add("Reset hakkı yok");
            }
        }

        if (Json.Str(limits, "rateLimitReachedType", "rate_limit_reached_type") is var reached && reached.Length > 0)
            snap.Notes.Add($"Limit doldu: {reached}");
        return snap;
    }

    static void AddWindow(UsageSnapshot snap, JsonElement limits, string key)
    {
        if (Json.Prop(limits, key) is not JsonElement w) return;
        var mins = Json.Num(w, "windowDurationMins", "window_minutes") ?? 0;
        snap.Meters.Add(new UsageMeter
        {
            Label = mins switch
            {
                300 => "5 saatlik limit",
                10080 => "Haftalık limit",
                >= 1440 => $"{mins / 1440:0} günlük limit",
                > 0 => $"{mins / 60:0} saatlik limit",
                _ => key == "primary" ? "Kısa dönem limiti" : "Uzun dönem limiti"
            },
            UsedPercent = Json.Num(w, "usedPercent", "used_percent") ?? 0,
            ResetsAt = Json.Time(w, "resetsAt", "resets_at")
        });
    }

    // Son Codex oturumlarının kaydındaki en yeni token_count.rate_limits
    static UsageSnapshot? FromRollout()
    {
        var db = Path.Combine(Home, ".codex", "state_5.sqlite");
        if (!File.Exists(db)) return null;
        foreach (var row in WinSqlite.Query(db, "SELECT rollout_path, updated_at FROM threads ORDER BY updated_at DESC LIMIT 8", 2))
        {
            var path = row[0].StartsWith(@"\\?\") ? row[0][4..] : row[0];
            if (!File.Exists(path)) continue;
            List<string> lines;
            try { lines = TranscriptTail.ReadLines(path, 512 * 1024); }
            catch { continue; }
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("\"rate_limits\"")) continue;
                using var doc = TranscriptTail.TryParse(lines[i]);
                if (doc == null) continue;
                var payload = Json.Prop(doc.RootElement, "payload");
                if (payload is not JsonElement pl || Json.Prop(pl, "rate_limits") is not JsonElement rl) continue;
                var snap = Parse(rl, camel: false, "Son Codex oturumunun kaydı");
                if (Json.Time(doc.RootElement, "timestamp") is DateTime ts) snap.FetchedAt = ts;
                return snap;
            }
        }
        return null;
    }
}
