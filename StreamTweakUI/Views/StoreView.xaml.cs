using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using StreamTweak.ViewModels;
using System;
using Windows.System;

namespace StreamTweak.Views
{
    public sealed partial class StoreView : Page
    {
        public StoreViewModel ViewModel { get; } = new StoreViewModel();

        private bool _initialized;

        // OAuth and store domains whose navigation must stay in the main frame
        // (not redirected to the system browser).
        private static readonly string[] _oauthDomains =
        [
            "facebook.com",
            "accounts.google.com",
            "appleid.apple.com",
            "discord.com/oauth2",
        ];

        private const string ChromeUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

        // Client Hints values matching the UA above.
        // WebView2 sends its own Sec-CH-UA headers exposing "Microsoft Edge WebView2"
        // even when Settings.UserAgent is overridden — OAuth providers use these to
        // detect and block embedded browsers. We replace them for OAuth domains only.
        private const string SecChUa =
            "\"Not/A)Brand\";v=\"8\", \"Chromium\";v=\"134\", \"Google Chrome\";v=\"134\"";

        private const string SecChUaFullVersion =
            "\"Not/A)Brand\";v=\"8.0.0.0\", \"Chromium\";v=\"134.0.6998.89\", \"Google Chrome\";v=\"134.0.6998.89\"";

        // URL patterns for WebResourceRequested filter (one per OAuth provider host).
        private static readonly string[] _oauthFilterPatterns =
        [
            "https://accounts.google.com/*",
            "https://*.google.com/o/oauth2/*",
            "https://www.facebook.com/*",
            "https://m.facebook.com/*",
            "https://appleid.apple.com/*",
        ];

        // JS injected before any page script. Two responsibilities:
        //  1. Remove WebView2 fingerprinting tells (navigator.webdriver, window.chrome).
        //  2. Hide Facebook / Google / Apple OAuth login buttons on Instant Gaming —
        //     these providers block embedded browsers server-side (Facebook since
        //     Oct 2021, Google since Sep 2021, Apple by policy), so the buttons
        //     would only lead to a dead-end error. Discord OAuth still works
        //     (redirect flow, less aggressive embedded-browser detection) and is
        //     intentionally NOT hidden. Email login also remains available.
        //     The CSS is appended to <html> (always present) so it applies even
        //     before <head> exists; selectors are defensive and target the patterns
        //     most likely used by IG (href to /auth/{provider}, class names
        //     containing the provider, data-provider attributes).
        private const string AntiDetectionScript = """
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            if (!window.chrome) window.chrome = { runtime: {} };
            (function () {
                var css =
                    // Instant Gaming login modal — exact selectors observed in markup.
                    // The buttons use class="<provider> signin-btn-<short>" with href
                    // pointing to the registration page (not to the OAuth provider URL),
                    // so href-based selectors don't catch them. Match by class instead.
                    '[class*="signin-btn-fb" i],'        +
                    '[class*="signin-btn-google" i],'    +
                    '[class*="signin-btn-apple" i],'     +
                    // Defensive fallbacks (other pages / future markup changes).
                    'a[href*="auth/facebook" i],'        +
                    'a[href*="auth/google" i],'          +
                    'a[href*="auth/apple" i],'           +
                    'a[href*="login/facebook" i],'       +
                    'a[href*="login/google" i],'         +
                    'a[href*="login/apple" i],'          +
                    'a[href*="facebook.com/" i][href*="oauth" i],' +
                    'a[href*="accounts.google.com" i],'  +
                    'a[href*="appleid.apple.com" i],'    +
                    '[data-provider="facebook" i],'      +
                    '[data-provider="google" i],'        +
                    '[data-provider="apple" i]'          +
                    '{ display: none !important; }';
                function inject() {
                    try {
                        var root = document.head || document.documentElement;
                        if (!root) return false;
                        var style = document.createElement('style');
                        style.textContent = css;
                        root.appendChild(style);
                        return true;
                    } catch (e) { return false; }
                }
                // Attempt immediately. AddScriptToExecuteOnDocumentCreatedAsync fires
                // before <html> exists in some frames (about:blank, iframes, recaptcha
                // sandboxes), so we retry on DOMContentLoaded if the immediate attempt
                // fails. Both attempts are wrapped in try/catch so a failure in one
                // frame never breaks the page.
                if (!inject()) {
                    document.addEventListener('DOMContentLoaded', inject, { once: true });
                }
            })();
            """;

        public StoreView()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (!_initialized)
                _ = InitializeWebViewAsync();
        }

        private async System.Threading.Tasks.Task InitializeWebViewAsync()
        {
            try
            {
                var userDataPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StreamTweak", "WebView2");
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataPath, new CoreWebView2EnvironmentOptions());
                await StoreWebView.EnsureCoreWebView2Async(env);

                var wv = StoreWebView.CoreWebView2;
                if (wv == null) return;

                // 1. Override User-Agent so providers don't see "WebView2".
                wv.Settings.UserAgent = ChromeUserAgent;

                // 2. Remove navigator.webdriver and add window.chrome before any page JS runs.
                await wv.AddScriptToExecuteOnDocumentCreatedAsync(AntiDetectionScript);

                // 3. Replace Sec-CH-UA Client Hints headers for OAuth provider domains.
                //    Settings.UserAgent doesn't affect these — they expose "Microsoft Edge WebView2".
                foreach (string pattern in _oauthFilterPatterns)
                    wv.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
                wv.WebResourceRequested += OnOAuthWebResourceRequested;

                wv.NavigationStarting  += OnNavigationStarting;
                wv.NavigationCompleted += OnNavigationCompleted;
                wv.NewWindowRequested  += OnNewWindowRequested;

                _initialized = true;

                wv.Navigate(StoreViewModel.HomeUrl);
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    WebView2ErrorBar.Message = ex.Message;
                    WebView2ErrorBar.IsOpen  = true;
                });
            }
        }

        // ── New-window handling ───────────────────────────────────────────────

        /// <summary>
        /// All window.open() calls are absorbed: the target URL is loaded in the main
        /// frame instead of a popup. OAuth providers (Facebook, Google, Apple, Discord)
        /// redirect back to Instant Gaming after login, so the redirect flow works
        /// without needing window.opener. Using a second CoreWebView2 via args.NewWindow
        /// is avoided because of a known WebView2 bug that causes a flash + no-op.
        /// </summary>
        private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            args.Handled = true;
            string url = args.Uri;
            DispatcherQueue.TryEnqueue(() => StoreWebView.CoreWebView2?.Navigate(url));
        }

        // ── Main frame navigation ─────────────────────────────────────────────

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            string url = args.Uri;

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return;

            if (url.Contains("instant-gaming.com", StringComparison.OrdinalIgnoreCase))
            {
                // All IG navigation passes through untouched.
            }
            else if (IsOAuthUrl(url))
            {
                // OAuth redirect — allow in main frame.
                // After login the provider redirects back to IG, restoring the session.
            }
            else
            {
                // Any other external URL: open in system browser.
                args.Cancel = true;
                _ = Launcher.LaunchUriAsync(new Uri(url));
            }
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ViewModel.CanGoBack    = StoreWebView.CoreWebView2?.CanGoBack    ?? false;
                ViewModel.CanGoForward = StoreWebView.CoreWebView2?.CanGoForward ?? false;
                ViewModel.CurrentUrl   = StoreWebView.Source?.ToString() ?? string.Empty;
            });
        }

        // ── Shared helpers ────────────────────────────────────────────────────

        private static bool IsOAuthUrl(string url)
        {
            foreach (string domain in _oauthDomains)
                if (url.Contains(domain, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static void OnOAuthWebResourceRequested(
            CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var headers = args.Request.Headers;

            if (headers.Contains("Sec-CH-UA"))
                headers.SetHeader("Sec-CH-UA", SecChUa);

            if (headers.Contains("Sec-CH-UA-Full-Version-List"))
                headers.SetHeader("Sec-CH-UA-Full-Version-List", SecChUaFullVersion);

            headers.SetHeader("Sec-CH-UA-Mobile", "?0");
        }

        // ── Toolbar buttons ───────────────────────────────────────────────────

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.CanGoBack) StoreWebView.GoBack();
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.CanGoForward) StoreWebView.GoForward();
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            StoreWebView.CoreWebView2?.Navigate(StoreViewModel.HomeUrl);
        }

        private void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
        {
            string url = string.IsNullOrEmpty(ViewModel.CurrentUrl)
                ? StoreViewModel.HomeUrl
                : StoreViewModel.InjectAffiliate(ViewModel.CurrentUrl);
            _ = Launcher.LaunchUriAsync(new Uri(url));
        }

        // ── Info banner ───────────────────────────────────────────────────────

        // Fires only when the user clicks the InfoBar's × button.
        // Persists the dismissal so the banner never appears again.
        private void OAuthNotice_CloseButtonClick(InfoBar sender, object args)
        {
            ViewModel.DismissOAuthNotice();
        }
    }
}
