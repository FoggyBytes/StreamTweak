using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace StreamTweak.Nvidia
{
    /// <summary>
    /// Resolves the globally-installed NVIDIA NGX DLL versions (DLSS Super Resolution,
    /// Ray Reconstruction, Frame Generation) and substitutes them into catalog value
    /// labels that contain the ${DlssVersion} / ${DlssdVersion} / ${DlssgVersion}
    /// placeholders (e.g. "On - DLSS-RR overridden by latest installed (${DlssdVersion})").
    ///
    /// The DLLs live in the driver store; their directory is published in the registry
    /// at HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore : FullPath. A given DLL may be
    /// absent (e.g. only FG installed globally) — in that case the corresponding
    /// "(... )" parenthetical is removed so no broken placeholder is shown.
    /// Resolved lazily once (the values are stable per driver install).
    /// </summary>
    public static class NgxVersions
    {
        private static readonly object _lock = new();
        private static bool _resolved;
        private static string _dlss = "";   // nvngx_dlss.dll  → ${DlssVersion}  (Super Resolution)
        private static string _dlssd = "";  // nvngx_dlssd.dll → ${DlssdVersion} (Ray Reconstruction)
        private static string _dlssg = "";  // nvngx_dlssg.dll → ${DlssgVersion} (Frame Generation)

        public static string DlssVersion  { get { Resolve(); return _dlss; } }
        public static string DlssdVersion { get { Resolve(); return _dlssd; } }
        public static string DlssgVersion { get { Resolve(); return _dlssg; } }

        /// <summary>
        /// Replaces the three DLSS version placeholders in a label. When a version is
        /// known it becomes e.g. "v310.2.1"; when unknown the surrounding " (placeholder)"
        /// parenthetical is stripped (falling back to a bare token removal otherwise).
        /// </summary>
        public static string Substitute(string label)
        {
            if (string.IsNullOrEmpty(label) || label.IndexOf("${", StringComparison.Ordinal) < 0)
                return label;

            label = SubstituteOne(label, "DlssVersion",  DlssVersion);
            label = SubstituteOne(label, "DlssdVersion", DlssdVersion);
            label = SubstituteOne(label, "DlssgVersion", DlssgVersion);
            return label;
        }

        private static string SubstituteOne(string label, string token, string version)
        {
            string placeholder = "${" + token + "}";
            if (!label.Contains(placeholder)) return label;

            if (!string.IsNullOrEmpty(version))
                return label.Replace(placeholder, "v" + version);

            // Unknown version: drop " (placeholder)" if present, else just remove the token.
            string stripped = Regex.Replace(label, @"\s*\(\s*" + Regex.Escape(placeholder) + @"\s*\)", "");
            if (stripped != label) return stripped;
            return label.Replace(placeholder, "").Trim();
        }

        private static void Resolve()
        {
            if (_resolved) return;
            lock (_lock)
            {
                if (_resolved) return;
                try
                {
                    string dir = ReadNgxCorePath();
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        _dlss  = ReadDllVersion(Path.Combine(dir, "nvngx_dlss.dll"));
                        _dlssd = ReadDllVersion(Path.Combine(dir, "nvngx_dlssd.dll"));
                        _dlssg = ReadDllVersion(Path.Combine(dir, "nvngx_dlssg.dll"));
                    }
                }
                catch { /* leave versions empty → placeholders stripped */ }
                _resolved = true;
            }
        }

        private static string ReadNgxCorePath()
        {
            // 64-bit view first, then WOW6432Node fallback.
            foreach (var key in new[]
            {
                @"SOFTWARE\NVIDIA Corporation\Global\NGXCore",
                @"SOFTWARE\WOW6432Node\NVIDIA Corporation\Global\NGXCore",
            })
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(key);
                    if (k?.GetValue("FullPath") is string p && !string.IsNullOrWhiteSpace(p))
                        return p;
                }
                catch { }
            }
            return "";
        }

        private static string ReadDllVersion(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                var fvi = FileVersionInfo.GetVersionInfo(path);
                // NGX DLLs report e.g. FileMajorPart=310, Minor=2, Build=1, Private=0.
                int major = fvi.FileMajorPart, minor = fvi.FileMinorPart, build = fvi.FileBuildPart;
                if (major == 0 && minor == 0 && build == 0)
                {
                    // Fall back to the raw string (commas → dots) if numeric parts are empty.
                    var raw = (fvi.ProductVersion ?? fvi.FileVersion ?? "").Replace(',', '.').Replace(" ", "");
                    return raw.Trim();
                }
                return $"{major}.{minor}.{build}";
            }
            catch { return ""; }
        }
    }
}
