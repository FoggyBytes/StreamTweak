using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StreamTweak.Services;

namespace StreamTweak.Views
{
    public sealed partial class GlossaryView : Page
    {
        public GlossaryView()
        {
            this.InitializeComponent();
        }

        // Deep-link: an ⓘ InfoHint parked a term in AppStateService before navigating here.
        // Read + clear it, then scroll that row into view (deferred until layout is ready).
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            string? term = AppStateService.Instance.PendingGlossaryTerm;
            AppStateService.Instance.PendingGlossaryTerm = null;
            if (string.IsNullOrEmpty(term)) return;

            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => ScrollToTerm(term));
        }

        private void ScrollToTerm(string term)
        {
            foreach (var child in GlossaryStack.Children)
            {
                if (child is GlossaryRow row &&
                    string.Equals(row.Term, term, StringComparison.OrdinalIgnoreCase))
                {
                    row.StartBringIntoView(new BringIntoViewOptions
                    {
                        VerticalAlignmentRatio = 0.15,
                        AnimationDesired = true,
                    });
                    row.Flash();
                    return;
                }
            }
        }
    }
}
