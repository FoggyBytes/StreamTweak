using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace StreamTweak
{
    public static class ToastHelper
    {
        private const string AppId = "FoggyBytes.StreamTweak";

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string AppID);

        /// <summary>
        /// Call once at startup before showing any toast.
        /// Sets the process AUMID and registers the app in the registry
        /// so Windows can route toast notifications correctly.
        /// </summary>
        public static void Initialize(string displayName, string iconPath)
        {
            try
            {
                SetCurrentProcessExplicitAppUserModelID(AppId);

                string regKey = $@"Software\Classes\AppUserModelId\{AppId}";
                Registry.SetValue($@"HKEY_CURRENT_USER\{regKey}", "DisplayName", displayName);
                Registry.SetValue($@"HKEY_CURRENT_USER\{regKey}", "IconUri", iconPath);
            }
            catch { }
        }

        /// <summary>
        /// Shows a WinRT toast with title, message, and optional attribution line.
        /// Silently swallows any failure — toasts are enhancement-only.
        /// </summary>
        public static void Show(string title, string message, string? attribution = null)
        {
            try
            {
                string attributionXml = string.IsNullOrEmpty(attribution)
                    ? string.Empty
                    : $"<text placement=\"attribution\">{EscapeXml(attribution)}</text>";

                string xml = $@"
<toast>
  <visual>
    <binding template=""ToastGeneric"">
      <text>{EscapeXml(title)}</text>
      <text>{EscapeXml(message)}</text>
      {attributionXml}
    </binding>
  </visual>
</toast>";

                var doc = new XmlDocument();
                doc.LoadXml(xml);

                var toast = new ToastNotification(doc);
                ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
            }
            catch { }
        }

        private static string EscapeXml(string input) =>
            input.Replace("&", "&amp;")
                 .Replace("<", "&lt;")
                 .Replace(">", "&gt;")
                 .Replace("\"", "&quot;")
                 .Replace("'", "&apos;");
    }
}
