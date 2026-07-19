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

        // Serialises read-modify-write cycles so two concurrent Set(...) calls
        // cannot clobber each other or collide on the shared .tmp file.
        private static readonly object _ioLock = new();

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

        // Returns null when the file is missing, empty, or unparseable (corrupt).
        // Treating a corrupt/blank config as "empty" is load-bearing: it lets Patch
        // fall back to a fresh JsonObject and REWRITE a valid file, healing corruption
        // instead of throwing inside Patch (whose catch used to swallow the exception,
        // silently aborting every write so NO setting could ever persist — the root
        // cause of the "toggle reverts on restart" bugs when config.json got blanked).
        private static JsonObject? LoadNode()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                string text = File.ReadAllText(ConfigPath);
                if (string.IsNullOrWhiteSpace(text)) return null;
                return JsonNode.Parse(text) as JsonObject;
            }
            catch { return null; }
        }

        private static void Patch(Action<JsonObject> mutate)
        {
            try
            {
                lock (_ioLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                    var obj = LoadNode() ?? new JsonObject();
                    mutate(obj);
                    string tmp = ConfigPath + ".tmp";
                    File.WriteAllText(tmp, obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    File.Move(tmp, ConfigPath, overwrite: true);
                }
            }
            catch (Exception ex) { StreamTweak.DebugLogger.Log($"[Config] Set failed: {ex.Message}"); }
        }
    }
}
