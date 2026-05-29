namespace StreamTweak.Nvidia
{
    /// <summary>
    /// One captured/loaded global-profile setting, surfaced to the UI.
    /// In diff-from-default snapshots every entry represents a value the user (or
    /// NVIDIA App) changed away from the driver default.
    /// </summary>
    public sealed class NvidiaSnapshotEntry
    {
        /// <summary>Raw 32-bit NVAPI setting ID.</summary>
        public uint SettingId { get; init; }

        /// <summary>Friendly setting name as reported by the driver (may be empty for unnamed/internal IDs).</summary>
        public string Name { get; init; } = "";

        /// <summary>The captured value, rendered as the .nip stores it (decimal dword, string, base64 binary…).</summary>
        public string Value { get; init; } = "";

        /// <summary>"Dword" | "AnsiString" | "String" | "Binary" | "Qword".</summary>
        public string ValueType { get; init; } = "Dword";

        /// <summary>Hex form of the setting ID, e.g. "0x10ECDB82" — handy for cross-referencing with NVPI.</summary>
        public string SettingIdHex => "0x" + SettingId.ToString("X8");
    }
}
