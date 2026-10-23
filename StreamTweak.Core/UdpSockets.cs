using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StreamTweak
{
    /// <summary>
    /// Which local UDP ports a set of processes holds (GetExtendedUdpTable). The streaming server
    /// holds UDP sockets only while a client streams (§65), so this is the "is a stream live?"
    /// probe — for LogParser.HasActiveStreamSockets, and since 10.0.0 also for the
    /// service, which compiles this file by link and asks it once more right before it restarts
    /// the server (SECURITY.md, Sync and restart). Moved out of LogParser unchanged.
    /// </summary>
    public static class UdpSockets
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UDPROW_OWNER_PID
        {
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwOwningPid;
        }

        // IPv6 rows carry a 16-byte address and a scope id before the port, and the fields are
        // laid out in a different order — hence a second struct rather than a shared one.
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UDP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] ucLocalAddr;
            public uint dwLocalScopeId;
            public uint dwLocalPort;
            public uint dwOwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen,
            bool sort, int ipVersion, int tblClass, uint reserved);

        private const int AF_INET = 2, AF_INET6 = 23, UDP_TABLE_OWNER_PID = 1;

        /// <summary>Local UDP ports currently bound by any of the given processes, both
        /// address families. Empty when there are none.</summary>
        public static int[] PortsOwnedBy(int[] pids)
        {
            var ports = new List<int>();
            Collect(AF_INET, pids, ports);
            Collect(AF_INET6, pids, ports);
            ports.Sort();
            return ports.ToArray();
        }

        private static void Collect(int family, int[] pids, List<int> ports)
        {
            int len = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref len, false, family, UDP_TABLE_OWNER_PID, 0);
            if (len <= 0) return;

            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (GetExtendedUdpTable(buf, ref len, false, family, UDP_TABLE_OWNER_PID, 0) != 0)
                    return;

                int rows = Marshal.ReadInt32(buf);
                int size = family == AF_INET
                    ? Marshal.SizeOf<MIB_UDPROW_OWNER_PID>()
                    : Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();

                for (int i = 0; i < rows; i++)
                {
                    IntPtr row = buf + 4 + i * size;
                    uint pid, port;
                    if (family == AF_INET)
                    {
                        var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(row);
                        pid = r.dwOwningPid; port = r.dwLocalPort;
                    }
                    else
                    {
                        var r = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(row);
                        pid = r.dwOwningPid; port = r.dwLocalPort;
                    }

                    if (Array.IndexOf(pids, (int)pid) < 0) continue;
                    // Network byte order, same as the TCP table.
                    ports.Add((int)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF)));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
