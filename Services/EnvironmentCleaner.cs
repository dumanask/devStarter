namespace DevStarter.Services;

// devStarter bir VS Code terminalinden (veya başka bir Electron uygulamasından) başlatılırsa
// ELECTRON_RUN_AS_NODE=1 gibi değişkenleri miras alır. Bunlar açtığımız programlara geçerse
// Code.exe editör yerine düz Node.js olarak çalışıp sessizce kapanır. Başlatılan her program
// bu sürecin ortamını devraldığı için açılışta bir kez temizlemek yeterli.
public static class EnvironmentCleaner
{
    public static void Clean()
    {
        try
        {
            var keys = Environment.GetEnvironmentVariables().Keys.Cast<object>()
                .Select(k => k.ToString() ?? "")
                .Where(IsInherited)
                .ToList();
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
        }
        catch (Exception ex) { Log.Error(ex, "env cleanup"); }
    }

    static bool IsInherited(string key)
        => key.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase)
        || key.Equals("CHROME_CRASHPAD_PIPE_NAME", StringComparison.OrdinalIgnoreCase);
}
