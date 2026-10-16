using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamTweak
{
    /// <summary>
    /// Read-only SQLite through Windows' own engine, winsqlite3.dll (System32, signed by
    /// Microsoft, present since Windows 10 1507). Enough for the one place StreamTweak reads a
    /// database — GOG Galaxy's galaxy-2.0.db, in StoreCoverFetcher — and nothing more.
    ///
    /// Until 9.2.3 this went through Microsoft.Data.Sqlite, which ships SQLitePCLRaw (three
    /// assemblies) and its own native e_sqlite3.dll: four files published unsigned, which Smart
    /// App Control can stop and the SignPath Foundation may not sign for us (§92.16). Windows'
    /// engine is serviced by Windows Update, so the SQLite version pin that closed
    /// CVE-2025-6965 went with them.
    ///
    /// Values come back the way SqliteCommand.ExecuteScalar gave them, so the callers kept
    /// their `is long` / `is string` tests: INTEGER → long, TEXT → string, FLOAT → double,
    /// BLOB → byte[], NULL → null. Any SQLite error throws InvalidOperationException.
    /// </summary>
    internal sealed class WinSqliteDb : IDisposable
    {
        private IntPtr _db;

        private WinSqliteDb(IntPtr db) => _db = db;

        public static WinSqliteDb OpenReadOnly(string path)
        {
            int rc = NativeMethods.sqlite3_open_v2(Utf8(path), out IntPtr db, SQLITE_OPEN_READONLY, IntPtr.Zero);
            if (rc != SQLITE_OK)
            {
                // open_v2 hands back a handle even when it fails, and it must still be closed.
                string message = db != IntPtr.Zero ? ErrorMessage(db) : $"error {rc}";
                NativeMethods.sqlite3_close_v2(db);
                throw new InvalidOperationException($"SQLite open failed: {message}");
            }
            // What Microsoft.Data.Sqlite did through its 30 s command timeout: wait for a writer
            // (GOG Galaxy itself) instead of failing on the first SQLITE_BUSY.
            NativeMethods.sqlite3_busy_timeout(db, 30_000);
            return new WinSqliteDb(db);
        }

        /// <summary>First column of the first row, or null when there is no row.</summary>
        public object? Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            object? result = null;
            Run(sql, parameters, stmt => { result = ColumnValue(stmt, 0); return false; });
            return result;
        }

        /// <summary>Every row, every column. For the small result sets this class is meant for.</summary>
        public List<object?[]> Rows(string sql, params (string Name, object Value)[] parameters)
        {
            var rows = new List<object?[]>();
            Run(sql, parameters, stmt =>
            {
                int count = NativeMethods.sqlite3_column_count(stmt);
                var row = new object?[count];
                for (int i = 0; i < count; i++) row[i] = ColumnValue(stmt, i);
                rows.Add(row);
                return true;
            });
            return rows;
        }

        public void Dispose()
        {
            if (_db == IntPtr.Zero) return;
            NativeMethods.sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }

        // Prepares, binds, then calls onRow for each row until it returns false or the rows end.
        private void Run(string sql, (string Name, object Value)[] parameters, Func<IntPtr, bool> onRow)
        {
            ObjectDisposedException.ThrowIf(_db == IntPtr.Zero, this);

            int rc = NativeMethods.sqlite3_prepare_v2(_db, Utf8(sql), -1, out IntPtr stmt, IntPtr.Zero);
            if (rc != SQLITE_OK) throw Error("prepare");
            try
            {
                foreach (var (name, value) in parameters)
                {
                    int index = NativeMethods.sqlite3_bind_parameter_index(stmt, Utf8(name));
                    if (index == 0) throw new InvalidOperationException($"SQLite: no parameter {name}");
                    rc = value switch
                    {
                        string s => NativeMethods.sqlite3_bind_text(stmt, index, Encoding.UTF8.GetBytes(s), Encoding.UTF8.GetByteCount(s), SQLITE_TRANSIENT),
                        int i    => NativeMethods.sqlite3_bind_int64(stmt, index, i),
                        long l   => NativeMethods.sqlite3_bind_int64(stmt, index, l),
                        _        => throw new ArgumentException($"Unsupported SQLite parameter type {value.GetType().Name}"),
                    };
                    if (rc != SQLITE_OK) throw Error("bind");
                }

                while (true)
                {
                    rc = NativeMethods.sqlite3_step(stmt);
                    if (rc == SQLITE_DONE) return;
                    if (rc != SQLITE_ROW) throw Error("step");
                    if (!onRow(stmt)) return;
                }
            }
            finally
            {
                NativeMethods.sqlite3_finalize(stmt);
            }
        }

        private static object? ColumnValue(IntPtr stmt, int column)
        {
            switch (NativeMethods.sqlite3_column_type(stmt, column))
            {
                case SQLITE_INTEGER:
                    return NativeMethods.sqlite3_column_int64(stmt, column);
                case SQLITE_FLOAT:
                    return NativeMethods.sqlite3_column_double(stmt, column);
                case SQLITE_TEXT:
                {
                    // column_text first, then column_bytes: that order is the one SQLite documents
                    // as safe, since the text call may convert the value and change its length.
                    IntPtr text = NativeMethods.sqlite3_column_text(stmt, column);
                    int bytes = NativeMethods.sqlite3_column_bytes(stmt, column);
                    return text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(text, bytes);
                }
                case SQLITE_BLOB:
                {
                    IntPtr blob = NativeMethods.sqlite3_column_blob(stmt, column);
                    int bytes = NativeMethods.sqlite3_column_bytes(stmt, column);
                    var data = new byte[bytes];
                    if (bytes > 0) Marshal.Copy(blob, data, 0, bytes);
                    return data;
                }
                default:
                    return null;
            }
        }

        private InvalidOperationException Error(string what) =>
            new($"SQLite {what} failed: {ErrorMessage(_db)}");

        private static string ErrorMessage(IntPtr db) =>
            Marshal.PtrToStringUTF8(NativeMethods.sqlite3_errmsg(db)) ?? "unknown error";

        // Null-terminated UTF-8, which is what every sqlite3_* string argument takes.
        private static byte[] Utf8(string s)
        {
            var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
            Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
            return bytes;
        }

        private const int SQLITE_OK   = 0;
        private const int SQLITE_ROW  = 100;
        private const int SQLITE_DONE = 101;

        private const int SQLITE_INTEGER = 1;
        private const int SQLITE_FLOAT   = 2;
        private const int SQLITE_TEXT    = 3;
        private const int SQLITE_BLOB    = 4;

        private const int SQLITE_OPEN_READONLY = 0x00000001;

        // SQLITE_TRANSIENT: SQLite copies the bound text before the call returns.
        private static readonly IntPtr SQLITE_TRANSIENT = new(-1);

        private static class NativeMethods
        {
            private const string Lib = "winsqlite3.dll";
            // System32 only, on every import (the attribute does not apply to a class): the engine
            // is Windows' own, and a winsqlite3.dll dropped next to the exe must never be the one
            // that loads.
            private const DllImportSearchPath Sys = DllImportSearchPath.System32;

            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_close_v2(IntPtr db);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_bind_parameter_index(IntPtr stmt, byte[] name);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] text, int nBytes, IntPtr destructor);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_step(IntPtr stmt);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_finalize(IntPtr stmt);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_column_count(IntPtr stmt);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_column_type(IntPtr stmt, int column);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern long sqlite3_column_int64(IntPtr stmt, int column);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern double sqlite3_column_double(IntPtr stmt, int column);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int column);
            [DllImport(Lib), DefaultDllImportSearchPaths(Sys)] public static extern int sqlite3_column_bytes(IntPtr stmt, int column);
        }
    }
}
