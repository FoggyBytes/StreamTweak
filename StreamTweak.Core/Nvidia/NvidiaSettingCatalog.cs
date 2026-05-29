using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace StreamTweak.Nvidia
{
    /// <summary>
    /// Read-only value-label catalog parsed once (lazily) from the embedded NVIDIA
    /// Profile Inspector CustomSettingNames.xml (MIT © Orbmu2k). Maps a raw DRS value
    /// to a friendly label, e.g. Pre-Compile Shader Options 0x00000002 → "Medium",
    /// Vertical Sync 0x47814940 → "Force on".
    ///
    /// Coverage is partial (very new DLSS/driver settings may be absent) — callers
    /// fall back to the raw value when GetValueLabel returns null. Setting NAMES are
    /// taken from the driver elsewhere; this catalog is consulted only for value labels.
    /// </summary>
    public static class NvidiaSettingCatalog
    {
        private static readonly object _lock = new();
        private static bool _loaded;
        // settingId → (value → label)
        private static Dictionary<uint, Dictionary<uint, string>> _valueLabels = new();

        /// <summary>Forces the (potentially slow) XML parse + NGX version resolution to run now,
        /// so it can be done off the UI thread. Safe to call repeatedly.</summary>
        public static void Warm() => EnsureLoaded();

        /// <summary>Friendly label for a value, or null if unknown. value parsed as uint.</summary>
        public static string? GetValueLabel(uint settingId, uint value)
        {
            EnsureLoaded();
            if (_valueLabels.TryGetValue(settingId, out var map)
                && map.TryGetValue(value, out var label))
                return label;
            return null;
        }

        /// <summary>Friendly label for a value given as a string (decimal). Null if not parseable/unknown.</summary>
        public static string? GetValueLabel(uint settingId, string rawDecimalValue)
        {
            if (uint.TryParse(rawDecimalValue, out var v))
                return GetValueLabel(settingId, v);
            return null;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try { Load(); }
                catch { /* leave catalog empty — callers fall back to raw values */ }
                _loaded = true;
            }
        }

        private static void Load()
        {
            var asm = typeof(NvidiaSettingCatalog).Assembly;
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("CustomSettingNames.xml", StringComparison.OrdinalIgnoreCase));
            if (resName == null) return;

            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) return;

            var doc = XDocument.Load(stream);
            var map = new Dictionary<uint, Dictionary<uint, string>>();

            foreach (var setting in doc.Descendants("CustomSetting"))
            {
                var idText = (string)setting.Element("HexSettingID");
                if (!TryParseHex(idText, out uint settingId)) continue;

                var values = setting.Element("SettingValues");
                if (values == null) continue;

                Dictionary<uint, string> valueMap = null;
                foreach (var cv in values.Elements("CustomSettingValue"))
                {
                    var hexValue = (string)cv.Element("HexValue");
                    var label    = (string)cv.Element("UserfriendlyName");
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    if (!TryParseHex(hexValue, out uint val)) continue;

                    // Substitute ${DlssVersion}/${DlssdVersion}/${DlssgVersion} with the real
                    // installed NGX DLL versions (or strip the parenthetical if not installed).
                    string text = NgxVersions.Substitute(label.Trim());

                    valueMap ??= new Dictionary<uint, string>();
                    valueMap[val] = text;
                }

                if (valueMap != null && valueMap.Count > 0)
                    map[settingId] = valueMap;
            }

            _valueLabels = map;
        }

        private static bool TryParseHex(string? text, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
    }
}
