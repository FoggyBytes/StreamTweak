using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Microsoft.Management.Infrastructure;

namespace StreamTweak
{
    /// <summary>
    /// One selectable value of the adapter's *SpeedDuplex setting.
    /// <para><see cref="Mbps"/> is parsed from the driver's own display string, which is the only
    /// thing the wire protocol carries — display strings vary by vendor and can be localized, so
    /// clients match on the number and never on <see cref="DisplayKey"/>.</para>
    /// <para><see cref="Mbps"/> is 0 for "Auto Negotiation": it is a valid setting to restore, but
    /// not a speed a client can ask for.</para>
    /// </summary>
    public sealed record LinkSpeedOption(long Mbps, bool FullDuplex, string DisplayKey, string RegistryValue);

    public static class NetworkManager
    {
        // Wireless is deliberately absent. Wi-Fi has no fixed link speed to match — the PHY rate
        // floats with signal and modulation — and the micro-burst buffering this feature solves
        // happens where a faster *wired* link meets a slower one. Wi-Fi adapters also don't expose
        // *SpeedDuplex, so before 8.1.0 they could be selected but never actually did anything.
        private static readonly string[] ExcludedKeywords =
        [
            "loopback", "pseudo", "virtual", "miniport", "vpn",
            "bluetooth", "wan miniport", "wireguard", "6to4",
            "isatap", "teredo", "vmware", "virtualbox", "hyper-v",
            "microsoft kernel debug", "microsoft wi-fi direct",
            "microsoft hosted network", "npcap loopback",
            "tailscale", "wintun", "zerotier"
        ];

        /// <summary>
        /// Names of physical, operational <b>wired</b> adapters. CIM InterfaceType 6 = Ethernet.
        /// </summary>
        public static List<string> GetPhysicalAdapterNames()
        {
            var physicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var session = CimSession.Create(null);
                const string query = "SELECT Name FROM MSFT_NetAdapterSettingData WHERE InterfaceType = 6";
                var instances = session.QueryInstances(@"root\StandardCimv2", "WQL", query);
                foreach (var instance in instances)
                {
                    string? name = instance.CimInstanceProperties["Name"].Value?.ToString();
                    if (!string.IsNullOrEmpty(name)) physicalNames.Add(name);
                }
            }
            catch { }

            // CIM returned results → cross-reference with operational status. The keyword filter
            // runs here too: several tunnels (Tailscale, WireGuard) present as InterfaceType 6.
            if (physicalNames.Count > 0)
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(a => physicalNames.Contains(a.Name)
                             && a.OperationalStatus == OperationalStatus.Up
                             && !IsExcluded(a))
                    .Select(a => a.Name)
                    .ToList();
            }

            // CIM unavailable (no elevation, WMI provider issue) → type-based heuristics.
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(a => a.OperationalStatus == OperationalStatus.Up
                         && a.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                         && !IsExcluded(a))
                .Select(a => a.Name)
                .ToList();
        }

        /// <summary>
        /// Wired adapters whose driver actually exposes *SpeedDuplex — the only ones whose speed
        /// can be changed. Listing anything else would offer the user a control that does nothing
        /// (USB wireless dongles and virtual switches often pass the Ethernet test but have no
        /// speed setting).
        /// </summary>
        public static List<string> GetManageableAdapterNames()
            => GetPhysicalAdapterNames()
                .Where(n => GetSupportedSpeedOptions(n).Count > 0)
                .ToList();

        private static bool IsExcluded(NetworkInterface a)
        {
            string desc = a.Description.ToLowerInvariant();
            string name = a.Name.ToLowerInvariant();
            return ExcludedKeywords.Any(kw => desc.Contains(kw) || name.Contains(kw));
        }

        // ── Link speed: measured vs configured ────────────────────────────────────
        // These are two different things and the distinction is load-bearing. The *setting*
        // is what we capture and restore (an adapter left on "Auto Negotiation" must stay on
        // it, so it keeps adapting to cable and switch); the *measured* speed is what we show
        // the user, because "Auto Negotiation" says nothing about what they get back.

        /// <summary>Currently negotiated link speed in Mbps, or 0 when the adapter is down/absent.</summary>
        public static long GetCurrentLinkMbps(string adapterName)
        {
            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
                return ni?.OperationalStatus == OperationalStatus.Up ? ni.Speed / 1_000_000 : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// The adapter's current *SpeedDuplex setting, read from the driver rather than inferred
        /// from the measured speed. Returns null when the adapter has no such setting.
        /// </summary>
        public static LinkSpeedOption? GetCurrentSpeedSetting(string adapterName)
        {
            try
            {
                using CimSession session = CimSession.Create(null);
                var instances = session.QueryInstances(@"root\StandardCimv2", "WQL", SpeedDuplexQuery(adapterName));

                foreach (var instance in instances)
                {
                    string? display = instance.CimInstanceProperties["DisplayValue"].Value?.ToString();
                    string? registry = instance.CimInstanceProperties["RegistryValue"].Value switch
                    {
                        string[] arr when arr.Length > 0 => arr[0],
                        var v => v?.ToString()
                    };
                    if (string.IsNullOrEmpty(display) || string.IsNullOrEmpty(registry)) break;
                    return ToOption(display, registry);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Every value the driver accepts for *SpeedDuplex, with the speed parsed out of the
        /// display string. Bypasses localization of the property name itself (e.g. "Speed &amp;
        /// Duplex" vs "Velocità").
        /// </summary>
        public static List<LinkSpeedOption> GetSupportedSpeedOptions(string adapterName)
        {
            var options = new List<LinkSpeedOption>();

            try
            {
                using CimSession session = CimSession.Create(null);
                var instances = session.QueryInstances(@"root\StandardCimv2", "WQL", SpeedDuplexQuery(adapterName));

                foreach (var instance in instances)
                {
                    var displays = instance.CimInstanceProperties["ValidDisplayValues"].Value as string[];
                    var registries = instance.CimInstanceProperties["ValidRegistryValues"].Value as string[];

                    if (displays != null && registries != null && displays.Length == registries.Length)
                    {
                        for (int i = 0; i < displays.Length; i++)
                            options.Add(ToOption(displays[i], registries[i]));
                    }
                    break;
                }
            }
            catch { }

            return options;
        }

        /// <summary>
        /// The option to apply for a requested speed. Full duplex wins when a driver lists both,
        /// which it usually does — half duplex at the same rate would be a downgrade nobody asked for.
        /// </summary>
        public static LinkSpeedOption? FindOptionForMbps(IEnumerable<LinkSpeedOption> options, long mbps)
            => options.Where(o => o.Mbps == mbps)
                      .OrderByDescending(o => o.FullDuplex)
                      .FirstOrDefault();

        /// <summary>"2.5 Gbps", "100 Mbps", or "—" for 0.</summary>
        public static string FormatMbps(long mbps)
            => mbps <= 0 ? "—"
             : mbps >= 1000 ? $"{(mbps / 1000.0).ToString("0.##", CultureInfo.InvariantCulture)} Gbps"
             : $"{mbps} Mbps";

        // ── internals ─────────────────────────────────────────────────────────────

        // WQL escapes with a backslash, not by doubling the quote (see §22) — an adapter
        // renamed to something containing an apostrophe would otherwise break the query.
        private static string SpeedDuplexQuery(string adapterName)
        {
            string safe = adapterName.Replace("\\", "\\\\").Replace("'", "\\'");
            return "SELECT * FROM MSFT_NetAdapterAdvancedPropertySettingData " +
                   $"WHERE Name = '{safe}' AND RegistryKeyword = '*SpeedDuplex'";
        }

        private static readonly Regex SpeedPattern =
            new(@"(\d+(?:[.,]\d+)?)\s*(gbps|mbps)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static LinkSpeedOption ToOption(string display, string registryValue)
        {
            string lower = display.ToLowerInvariant();
            long mbps = 0;

            var m = SpeedPattern.Match(display);
            if (m.Success &&
                double.TryParse(m.Groups[1].Value.Replace(',', '.'),
                                NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                mbps = (long)Math.Round(m.Groups[2].Value.Equals("gbps", StringComparison.OrdinalIgnoreCase)
                    ? value * 1000
                    : value);
            }

            // "Auto Negotiation" has no speed and no duplex; treat it as full so it never loses
            // a FindOptionForMbps tie-break it can't actually participate in (Mbps stays 0).
            bool fullDuplex = !lower.Contains("half");

            return new LinkSpeedOption(mbps, fullDuplex, display, registryValue);
        }
    }
}
