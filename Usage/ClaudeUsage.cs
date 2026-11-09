using System.IO;
using System.Text.Json;

namespace DevStarter.Usage;

// Claude: ağ isteği atılmaz. Claude Code'un ~/.claude.json içine kaydettiği son kullanım bilgisi okunur
// (cachedUsageUtilization). Kimlik bilgisi dosyasına dokunulmaz.
sealed class ClaudeUsage : IUsageProvider
{
    static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public string Name => "Claude";

    public async Task<UsageSnapshot> FetchAsync(CancellationToken token)
    {
        var snap = new UsageSnapshot { Provider = Name, Source = "Claude Code'un son kaydı" };
        if (!File.Exists(File_))
        {
            snap.Error = "Claude Code bulunamadı.";
            return snap;
        }

        string text = "";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                text = await sr.ReadToEndAsync(token);
                using var probe = JsonDocument.Parse(text);
                break;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Claude Code dosyayı o an yeniden yazıyor olabilir
                await Task.Delay(300, token);
            }
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (Json.Prop(root, "oauthAccount") is JsonElement account)
        {
            snap.Plan = Json.Str(account, "planDisplayName");
            if (snap.Plan.Length == 0)
                snap.Plan = Json.Str(account, "organizationType").Replace("claude_", "").Replace('_', ' ');
        }

        if (Json.Prop(root, "cachedUsageUtilization") is not JsonElement cache)
        {
            snap.Error = "Henüz kayıtlı kullanım bilgisi yok. Claude Code'da /usage yazınca oluşur.";
            return snap;
        }
        if (Json.Num(cache, "fetchedAtMs") is double ms) snap.FetchedAt = Json.FromEpoch((long)ms);

        if (Json.Prop(cache, "utilization") is not JsonElement u) return snap;

        // Yeni biçim: limits[] (her limit türü için yüzde ve sıfırlanma); yoksa eski alanlar
        if (Json.Prop(u, "limits") is JsonElement limits && limits.ValueKind == JsonValueKind.Array && limits.GetArrayLength() > 0)
        {
            foreach (var l in limits.EnumerateArray())
            {
                if (Json.Prop(l, "is_active") is JsonElement active && active.ValueKind == JsonValueKind.False) continue;
                snap.Meters.Add(new UsageMeter
                {
                    Label = LimitLabel(Json.Str(l, "kind"), l),
                    UsedPercent = Json.Num(l, "percent", "utilization") ?? 0,
                    ResetsAt = Json.Time(l, "resets_at")
                });
            }
        }
        else
        {
            AddWindow(snap, u, "five_hour", "5 saatlik oturum");
            AddWindow(snap, u, "seven_day", "Haftalık (tüm modeller)");
            AddWindow(snap, u, "seven_day_opus", "Haftalık Opus");
            AddWindow(snap, u, "seven_day_sonnet", "Haftalık Sonnet");
        }

        if (Json.Prop(u, "extra_usage") is JsonElement extra && Json.Prop(extra, "is_enabled") is JsonElement on && on.ValueKind == JsonValueKind.True)
        {
            var used = Json.Num(extra, "used_credits");
            var limit = Json.Num(extra, "monthly_limit");
            var cur = Json.Str(extra, "currency");
            snap.Notes.Add(limit is double lim
                ? $"Ek kullanım açık: {used ?? 0:0.##} / {lim:0.##} {cur}"
                : "Ek kullanım açık");
        }
        return snap;
    }

    static void AddWindow(UsageSnapshot snap, JsonElement u, string key, string label)
    {
        if (Json.Prop(u, key) is not JsonElement w) return;
        snap.Meters.Add(new UsageMeter
        {
            Label = label,
            UsedPercent = Json.Num(w, "utilization") ?? 0,
            ResetsAt = Json.Time(w, "resets_at")
        });
    }

    static string LimitLabel(string kind, JsonElement l)
    {
        var model = Json.Prop(l, "scope") is JsonElement scope && Json.Prop(scope, "model") is JsonElement m
            ? Json.Str(m, "display_name", "name")
            : "";
        return kind switch
        {
            "weekly_all" => "Haftalık (tüm modeller)",
            "five_hour" or "session" or "five_hour_all" => "5 saatlik oturum",
            _ when model.Length > 0 => $"Haftalık {model}",
            "weekly_opus" => "Haftalık Opus",
            "weekly_sonnet" => "Haftalık Sonnet",
            _ => kind.Replace('_', ' ')
        };
    }
}
