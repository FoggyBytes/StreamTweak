using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace StreamTweak
{
    public sealed partial class StreamingAdjustmentWindow : Window
    {
        public StreamingAdjustmentWindow()
        {
            this.InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(TitleBarArea);

            ConfigurePresenter();
            SizeAndCenter();

            // Match caption button colours to the dark background.
            var tb = AppWindow.TitleBar;
            tb.ExtendsContentIntoTitleBar    = true;
            tb.ButtonBackgroundColor         = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor         = Colors.White;
            tb.ButtonInactiveForegroundColor = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
            tb.ButtonHoverBackgroundColor    = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            tb.ButtonPressedBackgroundColor  = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
        }

        private void ConfigurePresenter()
        {
            var presenter = OverlappedPresenter.Create();
            presenter.IsResizable    = false;
            presenter.IsMaximizable  = false;
            presenter.IsMinimizable  = false;
            presenter.IsAlwaysOnTop  = true;
            AppWindow.SetPresenter(presenter);
        }

        private void SizeAndCenter()
        {
            var hwnd  = WindowNative.GetWindowHandle(this);
            uint dpi  = GetDpiForWindow(hwnd);
            double sc = dpi / 96.0;

            int w = (int)(440 * sc);
            int h = (int)(190 * sc);
            AppWindow.Resize(new SizeInt32(w, h));

            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            int x = (display.WorkArea.Width  - w) / 2;
            int y = (display.WorkArea.Height - h) / 3;   // upper-third of screen
            AppWindow.Move(new PointInt32(x, y));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);
    }
}
