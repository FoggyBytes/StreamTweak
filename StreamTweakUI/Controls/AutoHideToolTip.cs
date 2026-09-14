using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StreamTweak.Controls
{
    /// <summary>
    /// A tooltip that closes itself <see cref="VisibleFor"/> after it opens.
    /// WinUI's ToolTip has no show duration: it stays open for as long as the pointer rests
    /// on its owner, and on a chart the user is reading that parks the hint over the data.
    /// Usage: <c>ctl:AutoHideToolTip.Text="Click to expand"</c> in place of
    /// <c>ToolTipService.ToolTip</c>, so every chart gets the same behaviour from one place.
    /// </summary>
    public static class AutoHideToolTip
    {
        private static readonly TimeSpan VisibleFor = TimeSpan.FromSeconds(5);

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached("Text", typeof(string), typeof(AutoHideToolTip),
                new PropertyMetadata(null, OnTextChanged));

        public static string? GetText(DependencyObject obj) => (string?)obj.GetValue(TextProperty);

        public static void SetText(DependencyObject obj, string? value) => obj.SetValue(TextProperty, value);

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not string text || string.IsNullOrEmpty(text))
            {
                ToolTipService.SetToolTip(d, null);
                return;
            }

            var tip = new ToolTip { Content = text };
            DispatcherQueueTimer? timer = null;

            // Restarted on every open, so a tooltip reopened by a fresh hover gets its own
            // full five seconds rather than whatever was left from the previous one.
            tip.Opened += (_, _) =>
            {
                if (timer == null)
                {
                    timer = tip.DispatcherQueue.CreateTimer();
                    timer.Interval    = VisibleFor;
                    timer.IsRepeating = false;
                    timer.Tick += (_, _) => tip.IsOpen = false;
                }
                timer.Stop();
                timer.Start();
            };
            tip.Closed += (_, _) => timer?.Stop();

            ToolTipService.SetToolTip(d, tip);
        }
    }
}
