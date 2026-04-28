using StreamTweak.Services;

namespace StreamTweak.ViewModels
{
    public sealed class StoreViewModel : ViewModelBase
    {
        public const string HomeUrl = "https://www.instant-gaming.com/?igr=gamer-7d53b1";
        private const string AffiliateParam = "igr=gamer-7d53b1";
        private const string Domain = "instant-gaming.com";

        // Persisted dismissal flag for the social-login info banner.
        // Once the user clicks the close button, it never appears again.
        private const string OAuthNoticeDismissedKey = "StoreOAuthNoticeDismissed";

        private bool _canGoBack;
        public bool CanGoBack
        {
            get => _canGoBack;
            set => SetProperty(ref _canGoBack, value);
        }

        private bool _canGoForward;
        public bool CanGoForward
        {
            get => _canGoForward;
            set => SetProperty(ref _canGoForward, value);
        }

        private string _currentUrl = HomeUrl;
        public string CurrentUrl
        {
            get => _currentUrl;
            set => SetProperty(ref _currentUrl, value);
        }

        private bool _isOAuthNoticeVisible = !ConfigService.GetBool(OAuthNoticeDismissedKey);
        public bool IsOAuthNoticeVisible
        {
            get => _isOAuthNoticeVisible;
            set => SetProperty(ref _isOAuthNoticeVisible, value);
        }

        /// <summary>
        /// Hides the social-login info banner and persists the dismissal,
        /// so it never appears again on subsequent launches.
        /// </summary>
        public void DismissOAuthNotice()
        {
            IsOAuthNoticeVisible = false;
            ConfigService.Set(OAuthNoticeDismissedKey, true);
        }

        // Injects igr= into any instant-gaming.com URL that doesn't already have it.
        // Handles existing query strings, fragments, and non-http schemes safely.
        public static string InjectAffiliate(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (!url.Contains(Domain, StringComparison.OrdinalIgnoreCase)) return url;
            if (url.Contains("igr=", StringComparison.OrdinalIgnoreCase)) return url;

            // Preserve fragment — affiliate param must come before #
            string fragment = string.Empty;
            int hashIdx = url.IndexOf('#');
            if (hashIdx >= 0)
            {
                fragment = url[hashIdx..];
                url = url[..hashIdx];
            }

            string separator = url.Contains('?') ? "&" : "?";
            return url + separator + AffiliateParam + fragment;
        }
    }
}
