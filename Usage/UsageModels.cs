using System.Globalization;
using System.Text.Json;

namespace DevStarter.Usage;

public sealed class UsageMeter
{
    public string Label { get; set; } = "";
    public double UsedPercent { get; set; }          // 0–100
    public DateTime? ResetsAt { get; set; }          // UTC
    public string Detail { get; set; } = "";
}

public sealed class UsageSnapshot
{
    public string Provider { get; set; } = "";
    public string Plan { get; set; } = "";
    public List<UsageMeter> Meters { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;   // verinin kaynakta alındığı an (UTC)
    public string Source { get; set; } = "";
    public string? Error { get; set; }
    public bool Unavailable { get; set; }                       // ör. uygulama kapalı: son kayıtlı değer gösterilir
}

interface IUsageProvider
{
    string Name { get; }
    Task<UsageSnapshot> FetchAsync(CancellationToken token);
}

static class Json
{
    public static JsonElement? Prop(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                return v;
        return null;
    }

    public static double? Num(JsonElement e, params string[] names)
    {
        var v = Prop(e, names);
        if (v is not JsonElement x) return null;
        if (x.ValueKind == JsonValueKind.Number) return x.GetDouble();
        if (x.ValueKind == JsonValueKind.String && double.TryParse(x.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    public static string Str(JsonElement e, params string[] names)
        => Prop(e, names) is JsonElement x && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";

    // ISO 8601 metni veya epoch (saniye / milisaniye) → UTC
    public static DateTime? Time(JsonElement e, params string[] names)
    {
        var v = Prop(e, names);
        if (v is not JsonElement x) return null;
        if (x.ValueKind == JsonValueKind.String)
        {
            var s = x.GetString() ?? "";
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) return dto.UtcDateTime;
            if (long.TryParse(s, out var n)) return FromEpoch(n);
            return null;
        }
        if (x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var num)) return FromEpoch(num);
        return null;
    }

    public static DateTime FromEpoch(long n)
        => n > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(n).UtcDateTime : DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime;
}
