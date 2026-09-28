using System;
using System.Runtime.InteropServices;
public static class SqlitePathProbe
{
    const string Dll = @"D:\projects\SharpInspect.NET\tests\SharpInspect.Runtime.Tests\bin\x64\Release\net6.0\runtimes\win-x64\native\e_sqlite3.dll";
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr cb, IntPtr state, out IntPtr error);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern void sqlite3_free(IntPtr ptr);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_backup_init(IntPtr dest, [MarshalAs(UnmanagedType.LPUTF8Str)] string dn, IntPtr source, [MarshalAs(UnmanagedType.LPUTF8Str)] string sn);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_backup_step(IntPtr backup, int count);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_backup_finish(IntPtr backup);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg(IntPtr db);
    public static string Run(string path)
    {
        IntPtr source = IntPtr.Zero, dest = IntPtr.Zero, backup = IntPtr.Zero, error;
        try {
            var sc = sqlite3_open_v2(":memory:", out source, 6, IntPtr.Zero);
            var ec = sqlite3_exec(source, "CREATE TABLE proof(value TEXT); INSERT INTO proof VALUES('preserve-failures');", IntPtr.Zero, IntPtr.Zero, out error);
            if (error != IntPtr.Zero) sqlite3_free(error);
            var dc = sqlite3_open_v2(path, out dest, 6, IntPtr.Zero);
            backup = sqlite3_backup_init(dest, "main", source, "main");
            var step = backup == IntPtr.Zero ? -1 : sqlite3_backup_step(backup, 256);
            return $"pathLength={path.Length}; journalLength={path.Length+8}; source={sc}; seed={ec}; open={dc}; backup_step={step}; error={Marshal.PtrToStringUTF8(sqlite3_errmsg(dest))}";
        } finally { if (backup != IntPtr.Zero) sqlite3_backup_finish(backup); if(dest != IntPtr.Zero) sqlite3_close(dest); if(source != IntPtr.Zero) sqlite3_close(source); }
    }
}
