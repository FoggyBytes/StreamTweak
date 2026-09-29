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

        // Search: hide the rows that don't match, and any section heading left with no rows.
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = SearchBox.Text.Trim();
            UIElement? heading = null, rule = null;
            bool sectionHasMatch = false;

            void CloseSection()
            {
                var v = sectionHasMatch ? Visibility.Visible : Visibility.Collapsed;
                if (heading != null) heading.Visibility = v;
                if (rule != null) rule.Visibility = v;
            }

            foreach (var child in GlossaryStack.Children)
            {
                if (child is GlossaryRow row)
                {
                    bool match = q.Length == 0
                        || row.Term.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || row.Definition.Contains(q, StringComparison.OrdinalIgnoreCase);
                    row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    sectionHasMatch |= match;
                }
                else if (child is TextBlock)
                {
                    CloseSection();
                    heading = child; rule = null; sectionHasMatch = false;
                }
                else if (child is Microsoft.UI.Xaml.Shapes.Rectangle)
                {
                    rule = child;
                }
            }
            CloseSection();
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
