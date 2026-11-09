using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevStarter.Agents;
using DevStarter.Services;

namespace DevStarter.Usage;

// Antigravity: yalnızca uygulama açıkken, kendi yerel dil sunucusundan (language_server.exe) okunur.
// Port ve CSRF token sürecin komut satırından alınır; token yalnızca bellekte tutulur, hiçbir yere yazılmaz.
// Sunucunun sertifikası kendinden imzalı olduğu için yalnızca 127.0.0.1 için kabul edilir.
sealed class AntigravityUsage : IUsageProvider
{
    const string Service = "/exa.language_server_pb.LanguageServerService/";
    const string Metadata = """{"metadata":{"ideName":"antigravity","extensionName":"antigravity","ideVersion":"unknown","locale":"en"}}""";

    public string Name => "Antigravity";

    public async Task<UsageSnapshot> FetchAsync(CancellationToken token)
    {
        var server = FindServer();
        if (server == null)
            return new UsageSnapshot { Provider = Name, Unavailable = true, Error = "Antigravity açık değil. Kota yalnızca Antigravity çalışırken okunabilir." };

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri?.Host == "127.0.0.1"
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        foreach (var port in server.Value.Ports)
        {
            foreach (var scheme in new[] { "https", "http" })
            {
                var baseUrl = $"{scheme}://127.0.0.1:{port}{Service}";
                using var status = await Call(http, baseUrl + "GetUserStatus", Metadata, server.Value.Csrf, token);
                if (status == null) continue;

                var snap = new UsageSnapshot { Provider = Name, Source = "Antigravity (yerel)" };
                ParseUserStatus(status.RootElement, snap);

                // Yeni sürümler kotayı gruplar hâlinde verir; varsa onu tercih et
                using var summary = await Call(http, baseUrl + "RetrieveUserQuotaSummary", """{"forceRefresh":true}""", server.Value.Csrf, token);
                if (summary != null)
                {
                    var grouped = new List<UsageMeter>();
                    ParseQuotaSummary(summary.RootElement, grouped);
                    if (grouped.Count > 0) snap.Meters = grouped;
                }
                if (snap.Meters.Count == 0) snap.Error = "Antigravity yanıt verdi ama kota bilgisi içermiyor.";
                return snap;
            }
        }
        return new UsageSnapshot { Provider = Name, Error = "Antigravity'nin yerel sunucusuna bağlanılamadı." };
    }

    readonly record struct Server(string Csrf, List<int> Ports);

    static Server? FindServer()
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (!p.ProcessName.StartsWith("language_server", StringComparison.OrdinalIgnoreCase)) continue;
                var cmd = ProcessUtil.CommandLine(p.Id);
                if (cmd == null || !Regex.IsMatch(cmd, @"--app_data_dir[=\s]+antigravity", RegexOptions.IgnoreCase)) continue;
                var csrf = Regex.Match(cmd, @"--csrf_token[=\s]+(\S+)");
                if (!csrf.Success) continue;
                var ports = TcpTable.ListeningPorts(p.Id);
                if (ports.Count == 0) continue;
                return new Server(csrf.Groups[1].Value.Trim('"'), ports);
            }
        }
        return null;
    }

    static async Task<JsonDocument?> Call(HttpClient http, string url, string body, string csrf, CancellationToken token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("Connect-Protocol-Version", "1");
            req.Headers.Add("X-Codeium-Csrf-Token", csrf);
            using var res = await http.SendAsync(req, token);
            if (res.StatusCode != HttpStatusCode.OK) return null;
            var text = await res.Content.ReadAsStringAsync(token);
            var doc = JsonDocument.Parse(text);
            // Connect hata gövdesi: {"code":"...","message":"..."}
            if (Json.Str(doc.RootElement, "code") is var code && code.Length > 0 && code != "ok")
            {
                doc.Dispose();
                return null;
            }
            return doc;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
        catch (Exception ex)
        {
            Log.Error(ex, "antigravity call");
            return null;
        }
    }

    static void ParseUserStatus(JsonElement root, UsageSnapshot snap)
    {
        var us = Json.Prop(root, "userStatus") ?? root;
        if (Json.Prop(us, "planStatus") is JsonElement ps && Json.Prop(ps, "planInfo") is JsonElement pi)
            snap.Plan = Json.Str(pi, "planDisplayName", "planName");
        if (snap.Plan.Length == 0) snap.Plan = Json.Str(us, "userTier");

        if (Json.Prop(us, "cascadeModelConfigData") is not JsonElement cfg
            || Json.Prop(cfg, "clientModelConfigs") is not JsonElement models
            || models.ValueKind != JsonValueKind.Array) return;
        foreach (var m in models.EnumerateArray())
        {
            if (Json.Prop(m, "quotaInfo") is not JsonElement q) continue;
            var remaining = Json.Num(q, "remainingFraction");
            if (remaining == null) continue;
            snap.Meters.Add(new UsageMeter
            {
                Label = Json.Str(m, "label") is var l && l.Length > 0 ? l : "Model",
                UsedPercent = Math.Clamp((1 - remaining.Value) * 100, 0, 100),
                ResetsAt = Json.Time(q, "resetTime")
            });
        }
    }

    static void ParseQuotaSummary(JsonElement root, List<UsageMeter> meters)
    {
        var resp = Json.Prop(root, "response") ?? root;
        if (Json.Prop(resp, "groups") is not JsonElement groups || groups.ValueKind != JsonValueKind.Array) return;
        foreach (var g in groups.EnumerateArray())
        {
            if (Json.Prop(g, "buckets") is not JsonElement buckets || buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (var b in buckets.EnumerateArray())
            {
                var remaining = Json.Prop(b, "remaining") is JsonElement r ? Json.Num(r, "remainingFraction") : Json.Num(b, "remainingFraction");
                if (remaining == null) continue;
                meters.Add(new UsageMeter
                {
                    Label = Json.Str(b, "displayName", "bucketId") is var l && l.Length > 0 ? l : Json.Str(g, "displayName", "groupId"),
                    UsedPercent = Math.Clamp((1 - remaining.Value) * 100, 0, 100),
                    ResetsAt = Json.Time(b, "resetTime")
                });
            }
        }
    }
}
