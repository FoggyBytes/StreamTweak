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
    /// The body below mirrors the compiler-generated Main verbatim. With
    /// DISABLE_XAML_GENERATED_MAIN the generated version is kept but renamed to
    /// <c>XamlGeneratedProgram.XamlGeneratedMain</c> — it is emitted without an
    /// access modifier (so, private) and therefore cannot be called from here.
    /// Hence the copy. It is four lines and has been stable across SDK versions,
    /// but if a future Windows App SDK changes its startup sequence, re-check
    /// App.g.i.cs in obj\ and mirror it again.
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

            ApplyXamlPerformanceOptIns();

            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            global::Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }

        /// <summary>
        /// Opts into the Windows App SDK 2.3 XAML optimisations that are safe for this app.
        ///
        /// Only four opt-ins exist in 2.3 (the "20+ optimisations" in the release notes are
        /// mostly always-on; the rest of that list are RuntimeCompatibility identifiers for
        /// servicing fixes, a different mechanism). Verified against the shipped enum
        /// Microsoft.UI.Xaml.Settings.XamlChangeId.
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
