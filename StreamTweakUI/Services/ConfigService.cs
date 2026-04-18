using System.Text.Json.Nodes;

namespace StreamTweak.Services
{
    /// <summary>
    /// Reads and writes config.json (%LOCALAPPDATA%\StreamTweak\config.json).
    /// Shares the same file as the WPF app: both projects read/write compatible JSON.
    /// Uses targeted key-patching so unrelated keys are never overwritten.
    /// </summary>
    public static class ConfigService
    {
        public static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamTweak", "config.json");

        // ── Read ──────────────────────────────────────────────────────────────

        public static string Get(string key, string defaultValue = "")
        {
            try
            {
                var node = LoadNode();
                return node?[key]?.GetValue<string>() ?? defaultValue;
            }
            catch { return defaultValue; }
        }

        public static bool GetBool(string key, bool defaultValue = false)
        {
            try
            {
                var node = LoadNode();
                return node?[key]?.GetValue<bool>() ?? defaultValue;
            }
            catch { return defaultValue; }
        }

        public static int GetInt(string key, int defaultValue = 0)
        {
            try
            {
                var node = LoadNode();
                return node?[key]?.GetValue<int>() ?? defaultValue;
            }
            catch { return defaultValue; }
        }

        // ── Write ─────────────────────────────────────────────────────────────

        public static void Set(string key, string value)  => Patch(obj => obj[key] = value);
        public static void Set(string key, bool value)    => Patch(obj => obj[key] = value);
        public static void Set(string key, int value)     => Patch(obj => obj[key] = value);

        // ── Internal ──────────────────────────────────────────────────────────

        private static JsonObject? LoadNode()
        {
            if (!File.Exists(ConfigPath)) return null;
            return JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject;
        }

        private static void Patch(Action<JsonObject> mutate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var obj = LoadNode() ?? new JsonObject();
                mutate(obj);
                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, ConfigPath, overwrite: true);
            }
            catch { }
        }
    }
}
