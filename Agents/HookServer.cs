using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DevStarter.Services;

namespace DevStarter.Agents;

// Codex / Gemini hook betiklerinin ve OpenCode eklentisinin durum bildirdiği yerel uç nokta.
// Yalnızca 127.0.0.1'i dinler; her isteğe gövdesiz 204 döner (hook çıktısı ajanın davranışını değiştirmesin).
// Çalıştığı sürece kilit dosyasını açık tutar: hook betikleri bu dosya silinebiliyorsa (devStarter kapalı)
// hiç bağlanmaya çalışmadan çıkar — Windows'ta kapalı porta bağlanmak ~2 sn sürüyor.
public sealed class HookServer
{
    public static readonly string HooksDir = Path.Combine(Log.Dir, "hooks");
    public static readonly string LockPath = Path.Combine(HooksDir, "devstarter.lock");
    const int MaxBody = 1 << 20;

    TcpListener? listener;
    FileStream? lockFile;
    CancellationTokenSource? cts;

    public int Port { get; private set; }
    public bool Running { get; private set; }
    public string Status { get; private set; } = "Başlatılmadı";

    // agent ("codex" | "gemini" | "opencode"), JSON gövde, isteği gönderen ajan süreci (bulunamazsa 0)
    public event Action<string, JsonElement, AgentProcess>? Received;

    // Gönderen süreci bulmak gerekiyor mu? (oturumun süreci zaten biliniyorsa aramayı atla)
    public Func<string, JsonElement, bool>? NeedsProcess { get; set; }

    public void Start(int port)
    {
        Stop();
        Port = port;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (SocketException ex)
        {
            listener = null;
            Running = false;
            Status = $"127.0.0.1:{port} kullanılamıyor ({ex.SocketErrorCode}). Başka bir program bu portu kullanıyor olabilir.";
            Log.Error(ex, "hook server");
            return;
        }

        try
        {
            Directory.CreateDirectory(HooksDir);
            lockFile = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (Exception ex) { Log.Error(ex, "hook lock"); }

        Running = true;
        Status = $"127.0.0.1:{port} dinleniyor";
        cts = new CancellationTokenSource();
        var token = cts.Token;
        var l = listener;
        _ = Task.Run(() => AcceptLoop(l, token));
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
        try { listener?.Stop(); } catch { }
        listener = null;
        try { lockFile?.Dispose(); } catch { }
        lockFile = null;
        try { if (File.Exists(LockPath)) File.Delete(LockPath); } catch { }
        Running = false;
    }

    async Task AcceptLoop(TcpListener l, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(token); }
            catch { return; }
            _ = Task.Run(() => Handle(client));
        }
    }

    async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                var stream = client.GetStream();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var (path, body) = await ReadRequest(stream, timeout.Token);
                var reply = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                if (path == null || !path.StartsWith("/hook/", StringComparison.OrdinalIgnoreCase) || body.Length == 0)
                {
                    await stream.WriteAsync(reply, timeout.Token);
                    return;
                }

                var agent = path[6..].Trim('/').ToLowerInvariant();
                using var doc = JsonDocument.Parse(body);

                // Yanıt vermeden önce gönderen süreci bul (curl hâlâ bağlıyken); oturum başına bir kez yeterli
                var proc = AgentProcess.None;
                var remotePort = (client.Client.RemoteEndPoint as IPEndPoint)?.Port ?? 0;
                if (remotePort > 0 && (NeedsProcess?.Invoke(agent, doc.RootElement) ?? true))
                    proc = AgentProcess.FromClient(TcpTable.OwnerOfLocalPort(remotePort));

                await stream.WriteAsync(reply, timeout.Token);
                Received?.Invoke(agent, doc.RootElement, proc);
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or SocketException) { }
            catch (Exception ex) { Log.Error(ex, "hook request"); }
        }
    }

    // Minimal HTTP/1.1: istek satırı, başlıklar, Content-Length veya chunked gövde
    static async Task<(string? Path, byte[] Body)> ReadRequest(NetworkStream s, CancellationToken token)
    {
        var buf = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await s.ReadAsync(chunk, token);
            if (n <= 0) return (null, Array.Empty<byte>());
            buf.Write(chunk, 0, n);
            if (buf.Length > MaxBody) return (null, Array.Empty<byte>());
            headerEnd = IndexOf(buf.GetBuffer(), (int)buf.Length, "\r\n\r\n"u8);
        }

        var all = buf.GetBuffer();
        var header = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = header.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2 || parts[0] != "POST") return (null, Array.Empty<byte>());
        string path = parts[1];

        int contentLength = -1;
        bool chunked = false;
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out contentLength);
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && value.Contains("chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
        }

        var rest = new MemoryStream();
        rest.Write(all, headerEnd + 4, (int)buf.Length - headerEnd - 4);

        if (!chunked)
        {
            if (contentLength < 0 || contentLength > MaxBody) return (path, Array.Empty<byte>());
            while (rest.Length < contentLength)
            {
                int n = await s.ReadAsync(chunk, token);
                if (n <= 0) break;
                rest.Write(chunk, 0, n);
            }
            var body = rest.ToArray();
            return (path, body.Length > contentLength ? body[..contentLength] : body);
        }

        // chunked: "<hex>\r\n<data>\r\n ... 0\r\n\r\n"
        while (IndexOf(rest.GetBuffer(), (int)rest.Length, "0\r\n\r\n"u8) < 0 && rest.Length < MaxBody)
        {
            int n = await s.ReadAsync(chunk, token);
            if (n <= 0) break;
            rest.Write(chunk, 0, n);
        }
        return (path, Dechunk(rest.ToArray()));
    }

    static byte[] Dechunk(byte[] data)
    {
        var output = new MemoryStream();
        int i = 0;
        while (i < data.Length)
        {
            int lineEnd = IndexOf(data[i..], data.Length - i, "\r\n"u8);
            if (lineEnd < 0) break;
            var sizeText = Encoding.ASCII.GetString(data, i, lineEnd).Split(';')[0].Trim();
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out int size) || size == 0) break;
            i += lineEnd + 2;
            if (i + size > data.Length) break;
            output.Write(data, i, size);
            i += size + 2;
        }
        return output.ToArray();
    }

    static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern)
        => data.AsSpan(0, length).IndexOf(pattern);
}

// Hook isteğini gönderen ajan süreci: curl → cmd → … → codex.exe / node.exe (gemini)
public readonly record struct AgentProcess(int Pid, long StartTicks, bool InVsCode)
{
    public static readonly AgentProcess None = new(0, 0, false);

    static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "curl", "cmd", "conhost", "powershell", "pwsh", "bash", "sh", "OpenConsole"
    };

    public static AgentProcess FromClient(int clientPid)
    {
        int pid = clientPid;
        for (int depth = 0; pid > 0 && depth < 6; depth++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!Wrappers.Contains(p.ProcessName))
                {
                    long start = 0;
                    try { start = p.StartTime.ToFileTimeUtc(); } catch { }
                    return new AgentProcess(pid, start, HasAncestor(pid, "Code"));
                }
            }
            catch { return None; }
            pid = ProcessUtil.ParentOf(pid);
        }
        return None;
    }

    static bool HasAncestor(int pid, string name)
    {
        for (int depth = 0; pid > 0 && depth < 8; depth++)
        {
            pid = ProcessUtil.ParentOf(pid);
            if (pid <= 0) return false;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return false; }
        }
        return false;
    }

    public bool IsAlive => Pid > 0 && ProcessUtil.IsAlive(Pid, StartTicks > 0 ? StartTicks.ToString() : null);
}
