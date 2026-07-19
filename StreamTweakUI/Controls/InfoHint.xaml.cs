using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using StreamTweak.Services;

namespace StreamTweak.Controls
{
    /// <summary>
    /// A small ⓘ affordance: hovering shows the concise <see cref="Tip"/> tooltip; clicking
    /// navigates to the Glossary and scrolls to <see cref="Term"/> (when set), so the full
    /// explanation lives in one place instead of as prose on every settings row.
    /// </summary>
    public sealed partial class InfoHint : UserControl
    {
        public static readonly DependencyProperty TipProperty =
            DependencyProperty.Register(nameof(Tip), typeof(string), typeof(InfoHint),
                new PropertyMetadata(string.Empty, (d, e) => ToolTipService.SetToolTip((InfoHint)d, e.NewValue)));

        public static readonly DependencyProperty TermProperty =
            DependencyProperty.Register(nameof(Term), typeof(string), typeof(InfoHint),
                new PropertyMetadata(string.Empty));

        /// <summary>Concise one-line explanation shown on hover.</summary>
        public string Tip
        {
            get => (string)GetValue(TipProperty);
            set => SetValue(TipProperty, value);
        }

        /// <summary>Glossary term to scroll to on click (must match a GlossaryRow.Term). Optional.</summary>
        public string Term
        {
            get => (string)GetValue(TermProperty);
            set => SetValue(TermProperty, value);
        }

        public InfoHint()
        {
            this.InitializeComponent();
            // Hand cursor so it reads as clickable.
            this.ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        }

        private void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            AppStateService.Instance.PendingGlossaryTerm = Term;
            App.MainWindow?.NavigateTo("Glossary");
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
            => Icon.Foreground = (Brush)Application.Current.Resources["STTextSecondary"];

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
            => Icon.Foreground = (Brush)Application.Current.Resources["STTextDim"];
    }
}
