using System;
using System.Runtime.InteropServices;

namespace StreamTweak
{
    /// <summary>
    /// The host's physical memory, in use and installed (10.0.0) — for the RAM card of StreamLight
    /// 8.0's Dashboard, sent in STATS as "ram_used" / "ram_total" (MB). One GlobalMemoryStatusEx
    /// call: cheap enough to make on every STATS request, so nothing is sampled in between.
    /// </summary>
    public static class SystemMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint  dwLength;
            public uint  dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        /// <summary>(used, total) in MB, or (-1, -1) when Windows does not answer.</summary>
        public static (long usedMb, long totalMb) Read()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return (-1, -1);
            long total = (long)(m.ullTotalPhys / (1024 * 1024));
            long used  = (long)((m.ullTotalPhys - m.ullAvailPhys) / (1024 * 1024));
            return (used, total);
        }
    }
}
