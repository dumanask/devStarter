using System.Runtime.InteropServices;
using System.Text;

namespace DevStarter.Discovery;

// Windows ile gelen winsqlite3.dll üzerinden salt okunur, minimal SQLite sorgusu (ek paket gerektirmez)
static class WinSqlite
{
    const string Dll = "winsqlite3.dll";
    const int SQLITE_OK = 0;
    const int SQLITE_ROW = 100;
    const int SQLITE_OPEN_READONLY = 0x00000001;

    public static List<string[]> Query(string dbPath, string sql, int columnCount)
    {
        var rows = new List<string[]>();
        var file = Encoding.UTF8.GetBytes(dbPath + "\0");
        if (sqlite3_open_v2(file, out var db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
        {
            if (db != IntPtr.Zero) sqlite3_close(db);
            return rows;
        }
        try
        {
            sqlite3_busy_timeout(db, 2000);
            if (sqlite3_prepare16_v2(db, sql, -1, out var stmt, IntPtr.Zero) != SQLITE_OK || stmt == IntPtr.Zero)
                return rows;
            try
            {
                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    var row = new string[columnCount];
                    for (int i = 0; i < columnCount; i++)
                        row[i] = Marshal.PtrToStringUni(sqlite3_column_text16(stmt, i)) ?? "";
                    rows.Add(row);
                }
            }
            finally { sqlite3_finalize(stmt); }
        }
        finally { sqlite3_close(db); }
        return rows;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    static extern int sqlite3_open_v2(byte[] filenameUtf8, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    static extern int sqlite3_close(IntPtr db);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    static extern int sqlite3_finalize(IntPtr stmt);
}
