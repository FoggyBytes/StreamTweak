using Microsoft.UI.Xaml.Settings;

namespace StreamTweak
{
    /// <summary>
    /// Custom entry point (enabled by <c>DISABLE_XAML_GENERATED_MAIN</c> in the .csproj).
    ///
    /// It exists for one reason: the Windows App SDK 2.3 XAML performance opt-ins
    /// (<see cref="XamlOptionalChanges"/>) must be applied BEFORE the XAML runtime
    /// initialises — i.e. before <c>Application.Start</c>. There is no later hook:
    /// by the time the <c>App</c> constructor runs, XAML is already up and the
    /// settings are locked (<c>XamlOptionalChanges.IsLocked()</c> returns true).
    ///
    /// With DISABLE_XAML_GENERATED_MAIN the compiler-generated Main is kept but renamed
    /// to <c>XamlGeneratedProgram.XamlGeneratedMain</c>, and this Main calls it once the
    /// opt-ins are in. It is <c>internal</c> since Windows App SDK 2.4.0 ("access
    /// modifier" fix in its release notes); until then it was emitted private, so up to
    /// 10.1.1 this file carried a copy of its body that had to be mirrored by hand
    /// whenever the SDK changed the startup sequence. Calling it keeps whatever the
    /// SDK generates (App.g.i.cs in obj\, which ends in App.g.cs's <c>new App()</c>).
    /// </summary>
    public static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Before anything logs: opt into the high-frequency detail (process scan, bridge
            // traffic, log rediscovery). Off by default — with it on, those paths accounted
            // for ~99% of debug.log and buried the events actually worth reading.
            DebugLogger.VerboseEnabled = Services.ConfigService.GetBool("VerboseLogging");

            // Read before SessionLogger.Initialize() runs (App ctor): the interrupted-session
            // recovery there applies this rule too.
            SessionLogger.RecordOnlyGameSessions = Services.ConfigService.GetBool("RecordOnlyGameSessions");

            // On unless the user turned it off: the client side starts off (§79.6).
            ClipboardShare.Enabled = Services.ConfigService.GetBool("ShareClipboard", true);

            ApplyXamlPerformanceOptIns();

            // COM wrappers, Application.Start, the dispatcher's SynchronizationContext and
            // new App() — the SDK's own sequence, generated at build time.
            XamlGeneratedProgram.XamlGeneratedMain();
        }

        /// <summary>
        /// Opts into the Windows App SDK XAML optimisations that are safe for this app.
        ///
        /// Only four opt-ins exist (the "20+ optimisations" in the release notes are
        /// mostly always-on; the rest of that list are RuntimeCompatibility identifiers for
        /// servicing fixes, a different mechanism). Verified against the shipped enum
        /// Microsoft.UI.Xaml.Settings.XamlChangeId.
        ///
        /// 2.4 adds none: the enum was read out of the metadata of both SDK builds and is
        /// byte-for-byte the same set — these four plus a _Reserved member. So there is no
        /// new opt-in to consider on the 2.3.1 → 2.4.0 move, and nothing here had to change.
        /// Nor does 2.6.5 (10.2.0): WinUI 2.3.6 and 2.6.5 list the same five members with the
        /// same values, read the same way on 07/11/2026.
        ///
        /// Every call is logged: EnableChange returns false if the setting was rejected or
        /// applied too late, so debug.log is the ground truth on whether these took effect.
        /// </summary>
        private static void ApplyXamlPerformanceOptIns()
        {
            try
            {
                // FontIcon / BitmapIcon skip an extra Grid in their visual tree.
                // Safe here: this app never walks the visual tree (no VisualTreeHelper usage),
                // and it uses a lot of FontIcons across the nav, tiles and host-setup rows.
                Log(XamlChangeId.IconNoGridOptimization);

                // Skips Style setters that don't apply a value, and defers applying
                // FrameworkElement/Control styles until the element is in the tree.
                // Safe here for the same reason: nothing reads styled properties off-tree.
                Log(XamlChangeId.OptimizeApplyStyles);

                // Skips initialising the default ContextFlyout on TextBlock / RichTextBlock.
                // Safe here: no TextBlock sets IsTextSelectionEnabled, so the default
                // right-click menu is never used. (The tray icon's ContextFlyout is an
                // explicitly assigned MenuFlyout and is unaffected.)
                Log(XamlChangeId.DeferContextFlyoutInit);

                // NOT enabled — swaps in new optimised XamlControlsResources styles,
                // including a rebuilt ScrollBar template. This app restyles heavily
                // (StreamTweakStyles.xaml: full Button ControlTemplates, ToggleSwitch and
                // accent/pill brush overrides, custom ListView item styles), which is exactly
                // the case Microsoft flags as at-risk. Enable it on its own and check the
                // scrollbars, buttons, toggles and list selection before keeping it.
                // Log(XamlChangeId.DefaultStyleOptimizations);
            }
            catch (Exception ex)
            {
                // Never let a perf opt-in stop the app from starting.
                DebugLogger.Log($"[XamlOptIn] failed: {ex}");
            }
        }

        private static void Log(XamlChangeId id)
        {
            bool ok = XamlOptionalChanges.EnableChange(id);
            DebugLogger.Log($"[XamlOptIn] {id} -> {(ok ? "enabled" : "REJECTED")}");
        }
    }
}
