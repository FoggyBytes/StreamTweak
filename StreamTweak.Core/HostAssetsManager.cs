using System;
using System.IO;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>
    /// Swaps the default tile PNGs (desktop.png / steam.png) in the streaming
    /// server's assets folder (Sunshine / Apollo / Vibeshine / Vibepollo) with
    /// the StreamTweak-bundled replacements, keeping the originals as
    /// desktop_backup.png / steam_backup.png so the change is fully reversible.
    /// All writes go through StreamTweakService (LocalSystem) because the host
    /// install folder lives under Program Files.
    /// </summary>
    public static class HostAssetsManager
    {
        /// <summary>
        /// Returns the &lt;HostInstallDir&gt;\assets directory for the detected
        /// streaming server, or null if no install is detected or the folder is missing.
        /// </summary>
        public static string? GetAssetsDirectory(StreamingAppInfo? info)
        {
            if (info?.ExePath is null) return null;
            string? installDir = Path.GetDirectoryName(info.ExePath);
            if (string.IsNullOrEmpty(installDir)) return null;
            string assets = Path.Combine(installDir, "assets");
            return Directory.Exists(assets) ? assets : null;
        }

        /// <summary>
        /// True when a *_backup.png from a previous swap is present, indicating that
        /// StreamTweak's tiles are currently applied.
        /// </summary>
        public static bool IsApplied(string assetsDir)
        {
            if (string.IsNullOrEmpty(assetsDir) || !Directory.Exists(assetsDir)) return false;
            return File.Exists(Path.Combine(assetsDir, "desktop_backup.png"))
                || File.Exists(Path.Combine(assetsDir, "steam_backup.png"));
        }

        public static async Task<bool> SwapAsync(string assetsDir, string desktopSource, string steamSource)
            => await Task.Run(() => SpeedChanger.SwapHostAssets(assetsDir, desktopSource, steamSource)
                                    || SwapDirect(assetsDir, desktopSource, steamSource));

        public static async Task<bool> RestoreAsync(string assetsDir)
            => await Task.Run(() => SpeedChanger.RestoreHostAssets(assetsDir)
                                    || RestoreDirect(assetsDir));

        // ── Dev-mode fallback (no service installed) ──────────────────────────
        // Will succeed only if the current user can write to the assets folder
        // (Program Files normally requires admin). The same fallback pattern is
        // used by SunshineSync.WriteAppsJson.

        private static bool SwapDirect(string assetsDir, string desktopSource, string steamSource)
        {
            try
            {
                BackupAndCopy(assetsDir, desktopSource, "desktop.png");
                BackupAndCopy(assetsDir, steamSource,   "steam.png");
                return true;
            }
            catch { return false; }
        }

        private static bool RestoreDirect(string assetsDir)
        {
            try
            {
                RestoreOne(assetsDir, "desktop.png");
                RestoreOne(assetsDir, "steam.png");
                return true;
            }
            catch { return false; }
        }

        private static void BackupAndCopy(string assetsDir, string source, string tileName)
        {
            string original = Path.Combine(assetsDir, tileName);
            string backup   = Path.Combine(assetsDir,
                Path.GetFileNameWithoutExtension(tileName) + "_backup.png");

            if (File.Exists(original) && !File.Exists(backup))
                File.Move(original, backup);
            else if (File.Exists(original) && File.Exists(backup))
                File.Delete(original);

            File.Copy(source, original, overwrite: true);
        }

        private static void RestoreOne(string assetsDir, string tileName)
        {
            string original = Path.Combine(assetsDir, tileName);
            string backup   = Path.Combine(assetsDir,
                Path.GetFileNameWithoutExtension(tileName) + "_backup.png");

            if (File.Exists(original)) File.Delete(original);
            if (File.Exists(backup))   File.Move(backup, original);
        }
    }
}
