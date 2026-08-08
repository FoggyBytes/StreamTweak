using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Linq;

namespace StreamTweak
{
    // ─── Model ──────────────────────────────────────────────────────────────────

    public class MonitorInfo
    {
        public uint   TargetId       { get; set; }
        public LUID   AdapterId      { get; set; }
        public string FriendlyName   { get; set; } = string.Empty;
        public string DevicePath     { get; set; } = string.Empty;
        // EDID values returned by DISPLAYCONFIG — many virtual displays do not expose EDID
        public ushort EdidManufactureId { get; set; }
        public ushort EdidProductCodeId  { get; set; }
        public string GdiDeviceName  { get; set; } = string.Empty;
        public bool   HdrEnabled     { get; set; }
        public bool   HdrSupported   { get; set; }
        public int    Width          { get; set; }
        public int    Height         { get; set; }
        public int    RefreshRateHz  { get; set; }

        /// <summary>
        /// True when the device path or EDID suggests a virtual display.
        /// Uses multiple heuristics: known vendor substrings, common virtual driver
        /// names and missing EDID (many virtual displays don't expose EDID).
        /// </summary>
        public bool IsVirtual
        {
            get
            {
                if (!string.IsNullOrEmpty(DevicePath))
                {
                    var d = DevicePath.ToLowerInvariant();
                    if (d.Contains("sudovda") || d.Contains("idd_") || d.Contains("mttvdd")
                        || d.Contains("vibe") || d.Contains("vibepollo") || d.Contains("vibeshine")
                        || d.Contains("moonlight") || d.Contains("vbox") || d.Contains("vmware")
                        || d.Contains("virtual") || d.Contains("root\\virtual") || d.Contains("root\\v")
                        || d.Contains("sunshine_virtual") || d.Contains("virtual_monitor"))
                        return true;
                }

                // Many virtual displays do not provide EDID information — treat missing
                // EDID as a strong indicator of a virtual display.
                if (EdidManufactureId == 0 && EdidProductCodeId == 0)
                    return true;

                return false;
            }
        }
    }

    // ─── P/Invoke structures ─────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int  HighPart;
    }

    // ─── Service ─────────────────────────────────────────────────────────────────

    public static class HdrService
    {
        // ── P/Invoke declarations ────────────────────────────────────────────────

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        // Overloads for each request type — avoids any manual AllocHGlobal.
        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(
            ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(
            ref DISPLAYCONFIG_SET_HDR_STATE requestPacket);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(
            string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, IntPtr wParam,
            string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        // ── Constants ────────────────────────────────────────────────────────────

        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const int  ERROR_INSUFFICIENT_BUFFER = 122;
        private const int  ERROR_INVALID_PARAMETER   = 87;
        private const int  ERROR_NOT_SUPPORTED       = 50;
        private const int  ENUM_CURRENT_SETTINGS = -1;

        private const uint GET_TARGET_NAME           = 2;
        private const uint GET_SOURCE_NAME           = 1;
        private const uint GET_ADVANCED_COLOR_INFO   = 9;
        private const uint SET_ADVANCED_COLOR_STATE  = 10;  // Windows 10 / 11 pre-24H2
        private const uint SET_HDR_STATE             = 16;  // Windows 11 24H2+ (build ≥ 26100)

        // ── Path/Mode structs — fixed total sizes to match the Windows SDK layout ─

        /// <summary>
        /// DISPLAYCONFIG_PATH_INFO — 72 bytes total.
        /// sourceInfo(20) + targetInfo(48) + flags(4).
        /// Only the fields we actually use are declared; Size=72 fills the rest.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Size = 72)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public LUID SourceAdapterId;    // offset  0 — 8 bytes
            public uint SourceId;           // offset  8 — 4 bytes
            public uint SourceModeInfoIdx;  // offset 12 — 4 bytes
            public uint SourceStatusFlags;  // offset 16 — 4 bytes
            public LUID TargetAdapterId;    // offset 20 — 8 bytes
            public uint TargetId;           // offset 28 — 4 bytes
            public uint TargetModeInfoIdx;  // offset 32 — 4 bytes
            // offset 36–71 → implicit padding (outputTechnology, rotation, scaling,
            //   refreshRate, scanLineOrdering, targetAvailable, statusFlags, path flags)
        }

        /// <summary>
        /// DISPLAYCONFIG_MODE_INFO — 64 bytes total.
        /// infoType(4) + id(4) + adapterId(8) + union(48).
        /// When InfoType==1 (source), union starts with width(4), height(4).
        /// Not used directly for resolution — we call EnumDisplaySettings instead.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint InfoType;   // 1=source, 2=target
            public uint Id;
            public LUID AdapterId;
            // union (48 bytes) — not accessed directly
        }

        // ── Device-info structs ───────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint Type;
            public uint Size;
            public LUID AdapterId;
            public uint Id;
        }

        /// <summary>Get target friendly name + device path (type=2).</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;     // 16 bytes
            public uint  Flags;                                  //  4 bytes
            public uint  OutputTechnology;                       //  4 bytes
            public ushort EdidManufactureId;                    //  2 bytes
            public ushort EdidProductCodeId;                    //  2 bytes
            public uint  ConnectorInstance;                      //  4 bytes
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string MonitorFriendlyDeviceName;             // 128 bytes
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string MonitorDevicePath;                     // 256 bytes
        }

        /// <summary>Get GDI device name like "\\.\DISPLAY1" (type=1).</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;  // 16 bytes
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string ViewGdiDeviceName;                  // 64 bytes
        }

        /// <summary>Read HDR state — works on all Windows 10/11 versions (type=9).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;  // 16 bytes
            public uint Value;                                //  4 bytes — bit0=supported, bit1=enabled
            public uint ColorEncoding;                        //  4 bytes
            public uint BitsPerColorChannel;                  //  4 bytes
        }

        /// <summary>Set HDR state — Windows 10 and Windows 11 before 24H2 (type=10).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;  // 16 bytes
            public uint Value;                                //  4 bytes — bit0=enableAdvancedColor
        }

        /// <summary>Set HDR state — Windows 11 24H2+ build ≥ 26100 (type=16).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_HDR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;  // 16 bytes
            public uint Value;                                //  4 bytes — bit0=enableHdr
        }

        /// <summary>Minimal DEVMODE for EnumDisplaySettings — resolution + refresh rate.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DmDeviceName;         //  64 bytes
            public ushort DmSpecVersion;         //   2
            public ushort DmDriverVersion;       //   2
            public ushort DmSize;                //   2
            public ushort DmDriverExtra;         //   2
            public uint   DmFields;              //   4
            public int    DmPositionX;           //   4  (POINTL.x)
            public int    DmPositionY;           //   4  (POINTL.y)
            public uint   DmDisplayOrientation;  //   4
            public uint   DmDisplayFixedOutput;  //   4
            public short  DmColor;               //   2
            public short  DmDuplex;              //   2
            public short  DmYResolution;         //   2
            public short  DmTTOption;            //   2
            public short  DmCollate;             //   2
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DmFormName;            //  64 bytes
            public ushort DmLogPixels;           //   2
            public uint   DmBitsPerPel;          //   4
            public uint   DmPelsWidth;           //   4  ← resolution
            public uint   DmPelsHeight;          //   4  ← resolution
            public uint   DmDisplayFlags;        //   4
            public uint   DmDisplayFrequency;    //   4  ← Hz
        }

        // ── Public async entry points ────────────────────────────────────────────

        public static Task<List<MonitorInfo>> GetMonitorsAsync()
            => Task.Run(GetMonitors);

        public static Task SetHdrAsync(LUID adapterId, uint targetId, bool enable)
            => Task.Run(() => SetHdr(adapterId, targetId, enable));

        public static Task<bool> GetAutoHdrAsync()
            => Task.Run(GetAutoHdr);

        public static Task SetAutoHdrAsync(bool enable)
            => Task.Run(() => SetAutoHdr(enable));

        // ── Implementation ───────────────────────────────────────────────────────

        private static List<MonitorInfo> GetMonitors()
        {
            var result = new List<MonitorInfo>();

            uint pathCount = 0, modeCount = 0;
            DISPLAYCONFIG_PATH_INFO[] paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
            bool queried = false;

            // The display set can change between sizing the buffers and filling them. Windows
            // reports that either as ERROR_INSUFFICIENT_BUFFER or — as it does on a headless host
            // whose virtual display comes and goes with every session, measured here — as
            // ERROR_INVALID_PARAMETER, because the counts it was handed no longer describe the
            // topology. Both mean "re-measure and try again"; neither means "no HDR". The previous
            // code turned them into an empty list with no exception, which the Dashboard then
            // rendered as a confident "HDR: Off".
            //
            // The wait matters as much as the retry: without it five attempts land inside the same
            // millisecond, i.e. inside the same unstable window, and all five fail.
            int lastErr = 0;
            // Eight spread over ~a second. Five at 80 ms covered 320 ms, and the log shows that
            // window closing with the topology still moving, over and over.
            for (int attempt = 0; attempt < 8 && !queried; attempt++)
            {
                if (attempt > 0) System.Threading.Thread.Sleep(120);

                // ⚠️ The sizing call gets the same treatment as the query below. It used to
                // `return` on any failure — from *inside* the retry loop — so a topology that
                // was unstable at this step was never retried at all, and the whole loop was
                // decorative. Both calls read the same live topology and both fail the same way
                // while it moves.
                int sizeErr = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
                if (sizeErr != 0)
                {
                    // ⚠️ ERROR_NOT_SUPPORTED (50) belongs here too, and it is the one this host
                    // actually returns — measured in debug.log, alongside the 87 the query gives.
                    // It comes back while the virtual display is being torn down and there is
                    // momentarily no active path to size at all. Transient, like the others.
                    lastErr = sizeErr;
                    if (sizeErr != ERROR_INSUFFICIENT_BUFFER && sizeErr != ERROR_INVALID_PARAMETER
                        && sizeErr != ERROR_NOT_SUPPORTED)
                    {
                        DebugLogger.Log($"[HDR] GetDisplayConfigBufferSizes failed: {sizeErr}");
                        return result;
                    }
                    continue;
                }

                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

                lastErr = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS,
                    ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (lastErr == 0) { queried = true; break; }

                if (lastErr != ERROR_INSUFFICIENT_BUFFER && lastErr != ERROR_INVALID_PARAMETER)
                {
                    DebugLogger.Log($"[HDR] QueryDisplayConfig failed: {lastErr}");
                    return result;
                }
            }

            if (!queried)
            {
                // Still unstable after five spread-out attempts. Returning empty is honest — the
                // caller must treat it as "couldn't tell", never as "no HDR".
                DebugLogger.Log($"[HDR] display topology kept changing (last error {lastErr}); giving up this round");
                return result;
            }

            // Key: (AdapterId.LowPart, AdapterId.HighPart, TargetId) — two monitors on different
            // adapters can share the same TargetId, so TargetId alone is not sufficient.
            var seenTargets = new HashSet<(uint, int, uint)>();

            for (int i = 0; i < pathCount; i++)
            {
                var path = paths[i];

                // Skip duplicate target entries (can happen with cloned outputs)
                var key = (path.TargetAdapterId.LowPart, path.TargetAdapterId.HighPart, path.TargetId);
                if (!seenTargets.Add(key)) continue;

                var m = new MonitorInfo
                {
                    TargetId  = path.TargetId,
                    AdapterId = path.TargetAdapterId,
                };

                // ── 2a. Friendly name + device path (target, type=2) ──────────
                var tName = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tName.Header.Type      = GET_TARGET_NAME;
                tName.Header.Size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                tName.Header.AdapterId = path.TargetAdapterId;
                tName.Header.Id        = path.TargetId;

                if (DisplayConfigGetDeviceInfo(ref tName) == 0)
                {
                    m.FriendlyName = tName.MonitorFriendlyDeviceName ?? string.Empty;
                    m.DevicePath   = tName.MonitorDevicePath          ?? string.Empty;
                    m.EdidManufactureId = tName.EdidManufactureId;
                    m.EdidProductCodeId = tName.EdidProductCodeId;
                }

                if (string.IsNullOrWhiteSpace(m.FriendlyName))
                    m.FriendlyName = $"Display {i + 1}";

                // ── 2b. GDI device name (source, type=1) — needed for EnumDisplaySettings ──
                var sName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                sName.Header.Type      = GET_SOURCE_NAME;
                sName.Header.Size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                sName.Header.AdapterId = path.SourceAdapterId;
                sName.Header.Id        = path.SourceId;

                if (DisplayConfigGetDeviceInfo(ref sName) == 0)
                    m.GdiDeviceName = sName.ViewGdiDeviceName ?? string.Empty;

                // ── 2c. Resolution + Hz (EnumDisplaySettings — simple & reliable) ──
                if (!string.IsNullOrEmpty(m.GdiDeviceName))
                {
                    var dm = new DEVMODE();
                    dm.DmSize = (ushort)Marshal.SizeOf<DEVMODE>();
                    if (EnumDisplaySettings(m.GdiDeviceName, ENUM_CURRENT_SETTINGS, ref dm))
                    {
                        m.Width         = (int)dm.DmPelsWidth;
                        m.Height        = (int)dm.DmPelsHeight;
                        m.RefreshRateHz = (int)dm.DmDisplayFrequency;
                    }
                }

                // ── 2d. HDR state (type=9 — works on all Windows 10/11 versions) ──
                var ci = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                ci.Header.Type      = GET_ADVANCED_COLOR_INFO;
                ci.Header.Size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
                ci.Header.AdapterId = path.TargetAdapterId;
                ci.Header.Id        = path.TargetId;

                if (DisplayConfigGetDeviceInfo(ref ci) == 0)
                {
                    bool supported = (ci.Value & 1u) != 0; // bit 0 = advancedColorSupported
                    bool wideColorEnf = (ci.Value & 4u) != 0; // bit 2 = wideColorEnforced (SDR/ACM)
                    bool colorEnabled = (ci.Value & 2u) != 0; // bit 1 = advancedColorEnabled

                    m.HdrSupported = supported;                              // toggle always enabled if the monitor supports HDR
                    m.HdrEnabled = supported && colorEnabled && !wideColorEnf; // HDR ON only when all three conditions are met
                }

                result.Add(m);
            }

            return result;
        }

        private static void SetHdr(LUID adapterId, uint targetId, bool enable)
        {
            int build = Environment.OSVersion.Version.Build;

            if (build >= 26100)
            {
                // Windows 11 24H2 and newer — use type 16 (SET_HDR_STATE)
                var req = new DISPLAYCONFIG_SET_HDR_STATE();
                req.Header.Type      = SET_HDR_STATE;
                req.Header.Size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_HDR_STATE>();
                req.Header.AdapterId = adapterId;
                req.Header.Id        = targetId;
                req.Value            = enable ? 1u : 0u;

                int ret = DisplayConfigSetDeviceInfo(ref req);
                if (ret != 0)
                    throw new InvalidOperationException(
                        $"SetHDRState (type 16) failed with error {ret}");
            }
            else
            {
                // Windows 10 and Windows 11 before 24H2 — use type 10 (SET_ADVANCED_COLOR_STATE)
                var req = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE();
                req.Header.Type      = SET_ADVANCED_COLOR_STATE;
                req.Header.Size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>();
                req.Header.AdapterId = adapterId;
                req.Header.Id        = targetId;
                req.Value            = enable ? 1u : 0u;

                int ret = DisplayConfigSetDeviceInfo(ref req);
                if (ret != 0)
                    throw new InvalidOperationException(
                        $"SetAdvancedColorState (type 10) failed with error {ret}");
            }
        }

        private static bool GetAutoHdr()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\DirectX\UserGpuPreferences");
                var val = key?.GetValue("DirectXUserGlobalSettings") as string ?? "";
                return val.Contains("AutoHDREnable=1");
            }
            catch { return false; }
        }

        private static void SetAutoHdr(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\DirectX\UserGpuPreferences", writable: true);

                var current = key.GetValue("DirectXUserGlobalSettings") as string ?? "";

                // Remove any existing AutoHDREnable entry
                var parts = current.Split(';')
                    .Where(p => !string.IsNullOrWhiteSpace(p) &&
                                !p.StartsWith("AutoHDREnable", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Insert the new value at the beginning
                parts.Insert(0, enable ? "AutoHDREnable=1" : "AutoHDREnable=0");

                key.SetValue("DirectXUserGlobalSettings",
                    string.Join(";", parts) + ";",
                    RegistryValueKind.String);
            }
            catch { }

            // Broadcast WM_SETTINGCHANGE
            try
            {
                SendMessageTimeout(
                    new IntPtr(-1), 0x001A, IntPtr.Zero,
                    "ImmersiveColorSet", 0x0002, 5000, out _);
            }
            catch { }
        }
    }
}
