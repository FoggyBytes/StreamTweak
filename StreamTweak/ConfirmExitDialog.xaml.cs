using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StreamTweak
{
    public partial class ConfirmExitDialog : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref uint attrValue, uint attrSize);

        private const uint DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;
        private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const uint DWMWCP_ROUND = 2;

        public bool Confirmed { get; private set; }

        public ConfirmExitDialog()
        {
            InitializeComponent();
            SourceInitialized += (_, _) => ApplyWindowStyling();
        }

        private void ApplyWindowStyling()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            uint dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            uint corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        private void Quit_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }
    }
}
