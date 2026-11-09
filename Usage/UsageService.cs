using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using DevStarter.Agents;
using DevStarter.Models;
using DevStarter.Services;

namespace DevStarter.Usage;

// Kullanım sağlayıcılarını paralel sorgular; son başarılı değerleri %APPDATA%\devStarter\usage-cache.json'da saklar
// (yalnızca yüzdeler, tarihler ve notlar — anahtar/token yok). UI iş parçacığından çağrılır.
public sealed class UsageService
{
    public static UsageService Instance { get; } = new();

    static readonly string CacheFile = Path.Combine(Log.Dir, "usage-cache.json");
    static readonly TimeSpan CodexInterval = TimeSpan.FromMinutes(10);

    readonly ClaudeUsage claude = new();
    readonly CodexUsage codex = new();
    readonly AntigravityUsage antigravity = new();
    readonly OpenCodeUsage openCode = new();
    readonly Dictionary<string, UsageSnapshot> cache = new();
    DateTime lastCodex;
    bool refreshing;

    public ObservableCollection<UsageCard> Cards { get; }
    public DateTime? LastRefresh { get; private set; }

    UsageService()
    {
        Cards = new ObservableCollection<UsageCard>(new[] { "Claude", "Codex", "Antigravity", "OpenCode" }.Select(n => new UsageCard(n)));
        LoadCache();
        foreach (var card in Cards)
            if (cache.TryGetValue(card.Name, out var snap)) card.Show(snap, fromCache: true, problem: null);
    }

    // force: panel açıldı veya "Yenile" → Codex ve OpenCode Go dahil hepsi
    public async Task RefreshAsync(bool force)
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var now = DateTime.UtcNow;
            bool doCodex = force || now - lastCodex > CodexInterval;
            openCode.IncludeNetwork = force;
            var work = new List<IUsageProvider> { claude, antigravity, openCode };
            if (doCodex) work.Add(codex);

            var cards = work.Select(p => Cards.First(c => c.Name == p.Name)).ToList();
            foreach (var c in cards) c.IsLoading = true;
            var results = await Task.WhenAll(work.Select(Run));
            for (int i = 0; i < results.Length; i++)
            {
                Apply(cards[i], results[i], networkIncluded: work[i] != openCode || force);
                cards[i].IsLoading = false;
            }
            if (doCodex) lastCodex = now;
            LastRefresh = DateTime.Now;
            SaveCache();
        }
        finally { refreshing = false; }
    }

    static async Task<UsageSnapshot> Run(IUsageProvider p)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { return await Task.Run(() => p.FetchAsync(cts.Token)); }
        catch (Exception ex)
        {
            Log.Error(ex, $"usage {p.Name}");
            return new UsageSnapshot { Provider = p.Name, Error = $"Okunamadı: {ex.Message}" };
        }
    }

    void Apply(UsageCard card, UsageSnapshot snap, bool networkIncluded)
    {
        // OpenCode Go yalnızca zorla yenilemede sorgulanır; aradaki yenilemelerde son Go değerlerini koru
        if (!networkIncluded && cache.TryGetValue(snap.Provider, out var prev) && snap.Error == null)
        {
            snap.Meters = prev.Meters;
            if (prev.Plan.Length > 0) snap.Plan = prev.Plan;
            snap.Notes.AddRange(prev.Notes.Where(n => n.StartsWith("OpenCode Go") || n.StartsWith("Abonelik")));
        }

        bool good = snap.Error == null && !snap.Unavailable && (snap.Meters.Count > 0 || snap.Notes.Count > 0);
        if (good)
        {
            cache[snap.Provider] = snap;
            card.Show(snap, fromCache: false, problem: null);
        }
        else if (cache.TryGetValue(snap.Provider, out var old))
        {
            card.Show(old, fromCache: true, problem: snap.Error);
        }
        else
        {
            card.Show(snap, fromCache: false, problem: snap.Error);
        }
    }

    public void Tick()
    {
        foreach (var c in Cards) c.Tick();
    }

    void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, UsageSnapshot>>(File.ReadAllText(CacheFile));
            if (data != null) foreach (var kv in data) cache[kv.Key] = kv.Value;
        }
        catch (Exception ex) { Log.Error(ex, "usage cache load"); }
    }

    void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            var tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch (Exception ex) { Log.Error(ex, "usage cache save"); }
    }
}

// Limitler sekmesindeki bir sağlayıcı kartı
public sealed class UsageCard : ObservableObject
{
    static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    UsageSnapshot snap = new();
    bool fromCache;
    string problem = "";
    bool loading;

    public UsageCard(string name) => Name = name;

    public string Name { get; }
    public string Glyph => Name switch { "Claude" => "✻", "Codex" => ">_", "Antigravity" => "A", _ => "OC" };
    public Brush Brush => Name switch
    {
        "Claude" => AgentColors.Kind(AgentKind.Claude),
        "Codex" => AgentColors.Kind(AgentKind.Codex),
        "Antigravity" => AgentColors.Kind(AgentKind.Gemini),
        _ => AgentColors.Kind(AgentKind.OpenCode)
    };

    public string Plan => snap.Plan;
    public bool HasPlan => snap.Plan.Length > 0;
    public List<MeterRow> Meters { get; private set; } = new();
    public List<string> Notes => snap.Notes;
    public bool HasNotes => snap.Notes.Count > 0;
    public string Problem => problem;
    public bool HasProblem => problem.Length > 0;
    public bool IsEmpty => Meters.Count == 0 && !HasNotes && !HasProblem;

    public bool IsLoading
    {
        get => loading;
        set { if (Set(ref loading, value)) Raise(nameof(Status)); }
    }

    public string Status
    {
        get
        {
            if (loading && snap.Source.Length == 0) return "Okunuyor…";
            if (snap.Source.Length == 0 && Meters.Count == 0) return "";
            var ago = AgentText.Duration(DateTime.UtcNow - snap.FetchedAt);
            var text = fromCache ? $"Son kayıtlı değer · {ago} önce" : $"{snap.Source} · {ago} önce alındı";
            return loading ? text + " · yenileniyor…" : text;
        }
    }

    public void Show(UsageSnapshot s, bool fromCache, string? problem)
    {
        snap = s;
        this.fromCache = fromCache;
        this.problem = problem ?? "";
        Meters = s.Meters.Select(m => new MeterRow(m)).ToList();
        foreach (var p in new[] { nameof(Plan), nameof(HasPlan), nameof(Meters), nameof(Notes), nameof(HasNotes),
                     nameof(Problem), nameof(HasProblem), nameof(IsEmpty), nameof(Status) })
            Raise(p);
    }

    public void Tick()
    {
        Raise(nameof(Status));
        foreach (var m in Meters) m.Tick();
    }

    public sealed class MeterRow(UsageMeter m) : ObservableObject
    {
        public string Label => m.Label;
        public double Percent => Math.Clamp(m.UsedPercent, 0, 100);
        public string PercentText => $"%{Percent:0}";

        public Brush BarBrush => Percent >= 90 ? Red : Percent >= 70 ? AgentColors.Waiting : AgentColors.Working;

        public string ResetText
        {
            get
            {
                if (m.ResetsAt is not DateTime r) return "";
                var left = r - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return "Sıfırlanmış olmalı (yenileyince güncellenir)";
                var local = r.ToLocalTime();
                var when = left.TotalDays < 6 ? local.ToString("ddd HH:mm", Tr) : local.ToString("d MMM HH:mm", Tr);
                return $"{AgentText.Duration(left)} sonra sıfırlanır · {when}";
            }
        }

        public void Tick() => Raise(nameof(ResetText));

        static readonly SolidColorBrush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
        static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    }
}
