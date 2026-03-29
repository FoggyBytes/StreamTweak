using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace StreamTweak
{
    public partial class ChartDetailWindow : Window
    {
        [DllImport("DwmApi")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public ChartDetailWindow(IReadOnlyList<float> data, string label, Brush color, SessionEntry session)
        {
            InitializeComponent();

            Title = $"{label} — StreamTweak";
            MetricLabel.Text = label;

            SubtitleLabel.Text = $"{session.StartTimeDisplay}  ·  {session.TelemetryDurationDisplay}";

            DetailChart.Points          = data;
            DetailChart.Label           = label;
            DetailChart.StrokeBrush     = color;
            DetailChart.DurationSeconds = session.SessionDurationSeconds;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, new[] { 1 }, sizeof(int));
        }
    }
}
