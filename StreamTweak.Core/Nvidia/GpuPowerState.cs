using System.Text.Json.Serialization;

namespace StreamTweak.Nvidia
{
    /// <summary>
    /// The NVIDIA GPU's power readings as they cross the named pipe (GpuPowerState), filled by
    /// StreamTweakService and read by the host metrics for the session timeline (9.2.0). Only
    /// what the timeline uses: the temperature comes from the host metrics' own D3DKMT reading.
    ///
    /// ⚠️ One file for both sides: StreamTweakService does not reference StreamTweak.Core,
    /// so its project compiles this file by link. Change it here only.
    /// All power figures are milliwatts; -1 / 0 mean "not known".
    /// </summary>
    public sealed class GpuPowerState
    {
        /// <summary>NVML answered and an NVIDIA GPU was found. False = no readings.</summary>
        [JsonPropertyName("available")] public bool Available { get; set; }

        /// <summary>The power limit in force, whoever set it (NVIDIA App, Afterburner…).</summary>
        [JsonPropertyName("enforced_mw")] public uint EnforcedMw { get; set; }
        [JsonPropertyName("power_mw")]    public int PowerMw { get; set; } = -1;

        /// <summary>The driver reports it is holding the clocks down to stay inside the power
        /// limit (NVML "SW Power Cap"): 1 = yes, 0 = no, -1 = this NVML cannot tell.</summary>
        [JsonPropertyName("power_capped")] public int PowerCapped { get; set; } = -1;
    }
}
