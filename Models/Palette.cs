using System.Windows;
using System.Windows.Media;

namespace DevStarter.Models;

public static class Palette
{
    public static readonly string[] Swatches =
    {
        "#6366F1", // indigo
        "#0EA5E9", // gök mavisi
        "#10B981", // zümrüt
        "#14B8A6", // turkuaz
        "#F59E0B", // kehribar
        "#F97316", // turuncu
        "#EF4444", // kırmızı
        "#EC4899", // pembe
        "#8B5CF6", // mor
        "#64748B"  // arduvaz
    };

    static readonly Dictionary<string, Brush> cache = new(StringComparer.OrdinalIgnoreCase);

    public static string ForName(string name)
    {
        int h = 0;
        foreach (char c in name ?? "") h = unchecked(h * 31 + char.ToLowerInvariant(c));
        return Swatches[(int)((uint)h % (uint)Swatches.Length)];
    }

    public static Brush BrushFor(string hex)
    {
        if (cache.TryGetValue(hex, out var cached)) return cached;
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); }
        catch { c = Colors.SlateGray; }

        var brush = new LinearGradientBrush(Mix(c, Colors.White, 0.16), Mix(c, Colors.Black, 0.22), new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        cache[hex] = brush;
        return brush;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    // "OyunProjem" → "OP", "Space Shooter" → "SS", "orbit" → "Or"
    public static string Initials(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "?";

        var words = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length >= 2)
            return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";

        var w = words[0];
        for (int i = 1; i < w.Length; i++)
            if (char.IsUpper(w[i]) || char.IsDigit(w[i]))
                return $"{char.ToUpperInvariant(w[0])}{w[i]}";

        return w.Length >= 2
            ? $"{char.ToUpperInvariant(w[0])}{char.ToLowerInvariant(w[1])}"
            : w.ToUpperInvariant();
    }
}
