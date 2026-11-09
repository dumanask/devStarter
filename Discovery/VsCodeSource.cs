using System.IO;
using System.Text.Json;
using DevStarter.Services;

namespace DevStarter.Discovery;

public record VsCodeEntry(string Name, string Path, bool IsWorkspaceFile, DateTime LastUsed);

// VS Code'un açtığı klasörler: %APPDATA%\Code\User\workspaceStorage\*\workspace.json
public static class VsCodeSource
{
    public static List<VsCodeEntry> Load()
    {
        var map = new Dictionary<string, VsCodeEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var codeUserDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code") + System.IO.Path.DirectorySeparatorChar;
            var root = System.IO.Path.Combine(codeUserDir, "User", "workspaceStorage");
            if (!Directory.Exists(root)) return new();

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var file = System.IO.Path.Combine(dir, "workspace.json");
                if (!File.Exists(file)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var folderUri = Str(doc.RootElement, "folder");
                    var wsUri = folderUri == null ? Str(doc.RootElement, "workspace") : null;
                    var uri = folderUri ?? wsUri;
                    var local = uri == null ? null : FileUriToPath(uri);
                    if (local == null) continue;

                    var path = NormalizeDrive(System.IO.Path.TrimEndingDirectorySeparator(local));
                    bool isWs = wsUri != null;
                    if (isWs ? !File.Exists(path) : !Directory.Exists(path)) continue;
                    // VS Code'un kendi iç çalışma alanları (ör. agent-sessions.code-workspace) proje değil
                    if (path.StartsWith(codeUserDir, StringComparison.OrdinalIgnoreCase)) continue;

                    var used = Directory.GetLastWriteTime(dir);
                    var state = System.IO.Path.Combine(dir, "state.vscdb");
                    if (File.Exists(state))
                    {
                        var t = File.GetLastWriteTime(state);
                        if (t > used) used = t;
                    }

                    if (!map.TryGetValue(path, out var existing) || existing.LastUsed < used)
                    {
                        var name = isWs ? System.IO.Path.GetFileNameWithoutExtension(path) : System.IO.Path.GetFileName(path);
                        map[path] = new VsCodeEntry(name.Length > 0 ? name : path, path, isWs, used);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Error(ex, "VS Code recent"); }

        return map.Values.OrderByDescending(e => e.LastUsed).ToList();
    }

    // VS Code "file:///c%3A/dev/x" yazar; .NET Uri kaçışlı ':' karakterini sürücü harfi olarak tanımaz
    static string? FileUriToPath(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null; // uzak (vscode-remote://) vb.
        var rest = Uri.UnescapeDataString(uri["file://".Length..]);
        if (rest.Length >= 3 && rest[0] == '/' && rest[2] == ':') rest = rest[1..];   // "/c:/dev" → "c:/dev"
        else if (!rest.StartsWith('/')) rest = "//" + rest;                          // "sunucu/paylaşım" → UNC
        return rest.Replace('/', '\\');
    }

    static string NormalizeDrive(string p)
        => p.Length >= 2 && p[1] == ':' ? char.ToUpperInvariant(p[0]) + p[1..] : p;

    static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
