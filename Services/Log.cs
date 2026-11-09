using System.IO;

namespace DevStarter.Services;

public static class Log
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "devStarter");

    public static void Error(Exception? ex, string? context = null)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {context} {ex}\r\n\r\n");
        }
        catch { }
    }
}
