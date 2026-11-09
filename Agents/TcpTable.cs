using System.Net;
using System.Runtime.InteropServices;

namespace DevStarter.Agents;

// IPv4 TCP bağlantı tablosu (iphlpapi GetExtendedTcpTable): hangi port hangi sürece ait
static class TcpTable
{
    public readonly record struct Row(int State, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int Pid);

    public const int Listen = 2;

    public static List<Row> Read()
    {
        var rows = new List<Row>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                int ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret == ERROR_INSUFFICIENT_BUFFER) continue;
                if (ret != 0) return rows;
                int count = Marshal.ReadInt32(buf);
                var ptr = buf + 4;
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                {
                    var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr + i * rowSize);
                    rows.Add(new Row((int)r.state, new IPAddress(r.localAddr), Port(r.localPort),
                        new IPAddress(r.remoteAddr), Port(r.remotePort), (int)r.owningPid));
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return rows;
    }

    // 127.0.0.1:<localPort> soketinin sahibi olan süreç (ör. hook isteğini gönderen curl)
    public static int OwnerOfLocalPort(int localPort)
    {
        foreach (var r in Read())
            if (r.LocalPort == localPort && IPAddress.IsLoopback(r.Local)) return r.Pid;
        return 0;
    }

    public static List<int> ListeningPorts(int pid)
        => Read().Where(r => r.Pid == pid && r.State == Listen).Select(r => r.LocalPort).Distinct().ToList();

    // Port, DWORD'ün düşük 16 bitinde ağ bayt sırasıyla durur
    static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_ALL = 5;
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);
}
