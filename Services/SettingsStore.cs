using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevStarter.Models;

namespace DevStarter.Services;

public static class SettingsStore
{
    public static readonly string FilePath = Path.Combine(Log.Dir, "settings.json");

    static readonly JsonSerializerOptions options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "settings load");
            // Bozuk dosyayı ezmeden kenara al
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Error(ex, "settings save"); }
    }
}
