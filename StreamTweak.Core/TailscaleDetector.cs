using System;
using System.Net.NetworkInformation;

namespace StreamTweak
{
    public static class TailscaleDetector
    {
        public static (bool detected, string ip) Detect()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!ni.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) &&
                        !ni.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            addr.Address.ToString().StartsWith("100."))
                            return (true, addr.Address.ToString());
                    }
                    return (true, "IP unknown");
                }
            }
            catch { }
            return (false, string.Empty);
        }
    }
}
