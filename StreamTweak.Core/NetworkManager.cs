using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using Microsoft.Management.Infrastructure;

namespace StreamTweak
{
    public static class NetworkManager
    {
        /// <summary>
        /// Returns the names of physical network adapters that are currently active (Up).
        /// Queries CIM for InterfaceType 6 (Ethernet) and 71 (WiFi), then cross-references
        /// with NetworkInterface to filter for operational ones only.
        /// </summary>
        public static List<string> GetPhysicalAdapterNames()
        {
            var physicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var session = CimSession.Create(null);
                const string query = "SELECT Name FROM MSFT_NetAdapterSettingData " +
                                     "WHERE InterfaceType = 6 OR InterfaceType = 71";
                var instances = session.QueryInstances(@"root\StandardCimv2", "WQL", query);
                foreach (var instance in instances)
                {
                    string? name = instance.CimInstanceProperties["Name"].Value?.ToString();
                    if (!string.IsNullOrEmpty(name)) physicalNames.Add(name);
                }
            }
            catch { }

            // CIM returned results → cross-reference with operational status.
            if (physicalNames.Count > 0)
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(a => physicalNames.Contains(a.Name)
                             && a.OperationalStatus == OperationalStatus.Up)
                    .Select(a => a.Name)
                    .ToList();
            }

            // CIM unavailable (e.g. no elevation, WMI provider issue) →
            // fall back to NetworkInterface type-based heuristics.
            // InterfaceType.Ethernet = 6, Wireless80211 = 71.
            string[] virtualKeywords = [
                "loopback", "pseudo", "virtual", "miniport", "vpn",
                "bluetooth", "wan miniport", "wireguard", "6to4",
                "isatap", "teredo", "vmware", "virtualbox", "hyper-v",
                "microsoft kernel debug", "microsoft wi-fi direct",
                "microsoft hosted network", "npcap loopback"
            ];

            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(a =>
                    a.OperationalStatus == OperationalStatus.Up &&
                    (a.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                     a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
                    !virtualKeywords.Any(kw =>
                        a.Description.ToLowerInvariant().Contains(kw) ||
                        a.Name.ToLowerInvariant().Contains(kw)))
                .Select(a => a.Name)
                .ToList();
        }

        /// <summary>
        /// Retrieves all supported speed values directly from the driver's *SpeedDuplex registry keyword.
        /// Bypasses localization issues (e.g., "Speed & Duplex" vs "Velocità").
        /// </summary>
        /// <param name="adapterName">The exact name of the network adapter (e.g., "Ethernet")</param>
        /// <returns>A dictionary containing the display name and its underlying registry value.</returns>
        public static Dictionary<string, string> GetSupportedSpeeds(string adapterName)
        {
            var supportedSpeeds = new Dictionary<string, string>();

            try
            {
                using CimSession session = CimSession.Create(null);

                // Query advanced properties targeting the universal *SpeedDuplex keyword
                string query = $"SELECT * FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE Name = '{adapterName}' AND RegistryKeyword = '*SpeedDuplex'";
                var instances = session.QueryInstances(@"root\StandardCimv2", "WQL", query);

                foreach (var instance in instances)
                {
                    var validDisplayValues = instance.CimInstanceProperties["ValidDisplayValues"].Value as string[];
                    var validRegistryValues = instance.CimInstanceProperties["ValidRegistryValues"].Value as string[];

                    if (validDisplayValues != null && validRegistryValues != null && validDisplayValues.Length == validRegistryValues.Length)
                    {
                        for (int i = 0; i < validDisplayValues.Length; i++)
                        {
                            supportedSpeeds.Add(validDisplayValues[i], validRegistryValues[i]);
                        }
                    }
                    break;
                }
            }
            catch { }

            return supportedSpeeds;
        }
    }
}
