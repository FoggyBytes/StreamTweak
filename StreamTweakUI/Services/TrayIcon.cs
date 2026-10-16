using System.Runtime.InteropServices;

namespace StreamTweak.Services
{
    /// <summary>
    /// One icon in the notification area, on Shell_NotifyIcon directly: a tooltip, a
    /// double-click, a native popup menu and an icon loaded from an .ico file — all StreamTweak
    /// ever asked of a tray library.
    ///
    /// Until 9.2.3 this was H.NotifyIcon.WinUI (2.1.0, pinned: 2.4 needs .NET 10), which ships
    /// three assemblies published unsigned — files Smart App Control can stop and the SignPath
    /// Foundation may not sign for us (§92.16). Its PopupMenu mode, the one App.Tray.cs used
    /// since 8.1.2, already drew the menu as a native HMENU, so the menu looks and behaves the
    /// same here.
    ///
    /// Create it on the UI thread: its hidden window lives there, so every callback runs on
    /// that thread's message loop, like the WinUI code around it.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        /// <summary>A menu entry; a null entry in the list is a separator.</summary>
        public sealed record MenuItem(string Text, Action Invoke);

        private const string ClassName = "StreamTweakTrayIcon";
        private const uint IconId = 1;
        private const uint CallbackMessage = WM_APP + 1;

        private readonly Action _onDoubleClick;
        private readonly IReadOnlyList<MenuItem?> _menu;
        // Kept in a field: the window calls back into it for as long as it exists, and a
        // collected delegate would crash the process on the next message.
        private readonly WndProcDelegate _wndProc;
        // Explorer broadcasts this when the taskbar is (re)created — after a crash or restart of
        // Explorer, and at logon when we started before it (Priority start, §66). Every icon must
        // be added again then.
        private readonly uint _taskbarCreated;
        private IntPtr _hwnd;
        private IntPtr _icon;
        private string _toolTip;
        private bool _added;

        public TrayIcon(string toolTip, Action onDoubleClick, IReadOnlyList<MenuItem?> menu)
        {
            _toolTip = toolTip;
            _onDoubleClick = onDoubleClick;
            _menu = menu;
            _wndProc = WndProc;
            _taskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

            IntPtr instance = NativeMethods.GetModuleHandleW(null);
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = ClassName,
            };
            NativeMethods.RegisterClassExW(ref wc);

            // A hidden TOP-LEVEL window, not a message-only one (HWND_MESSAGE): message-only
            // windows never receive broadcasts, and TaskbarCreated is one.
            _hwnd = NativeMethods.CreateWindowExW(WS_EX_TOOLWINDOW, ClassName, "StreamTweak tray", WS_POPUP,
                                                  0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"Tray window not created (error {Marshal.GetLastWin32Error()})");

            Add();
        }

        public string ToolTipText
        {
            get => _toolTip;
            set
            {
                _toolTip = value;
                Modify(NIF_TIP | NIF_SHOWTIP);
            }
        }

        /// <summary>Shows the .ico at <paramref name="path"/>, loaded at size × size pixels.</summary>
        public bool SetIcon(string path, int size)
        {
            IntPtr icon = NativeMethods.LoadImageW(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);
            if (icon == IntPtr.Zero) return false;

            IntPtr old = _icon;
            _icon = icon;
            Modify(NIF_ICON);
            // The shell keeps its own copy of what it was given, so the old handle is ours to free.
            if (old != IntPtr.Zero) NativeMethods.DestroyIcon(old);
            return true;
        }

        public void Dispose()
        {
            if (_hwnd == IntPtr.Zero) return;
            if (_added)
            {
                var data = Data(0);
                NativeMethods.Shell_NotifyIconW(NIM_DELETE, ref data);
                _added = false;
            }
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            NativeMethods.UnregisterClassW(ClassName, NativeMethods.GetModuleHandleW(null));
            if (_icon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }
        }

        // ── Shell_NotifyIcon ─────────────────────────────────────────────────

        // Fails while there is no taskbar yet (an early logon start); TaskbarCreated retries.
        private void Add()
        {
            uint flags = NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP | (_icon != IntPtr.Zero ? NIF_ICON : 0);
            var data = Data(flags);
            _added = NativeMethods.Shell_NotifyIconW(NIM_ADD, ref data);
            if (!_added) return;
            // Version 4: the callback carries the event in LOWORD(lParam) and the anchor point
            // in wParam, and WM_CONTEXTMENU arrives for the keyboard (Shift+F10, Menu) as well.
            // NIF_SHOWTIP above is what keeps the standard tooltip under this version.
            data.uVersion = NOTIFYICON_VERSION_4;
            NativeMethods.Shell_NotifyIconW(NIM_SETVERSION, ref data);
        }

        private void Modify(uint flags)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (!_added) { Add(); return; }
            var data = Data(flags);
            if (!NativeMethods.Shell_NotifyIconW(NIM_MODIFY, ref data))
            {
                // The icon is gone from the taskbar without a TaskbarCreated (rare): add it again.
                _added = false;
                Add();
            }
        }

        private NOTIFYICONDATAW Data(uint flags) => new()
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            // Windows caps the tooltip at 127 characters; the marshaller cuts anything longer.
            szTip = _toolTip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        // ── Window procedure ─────────────────────────────────────────────────

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                _added = false;
                Add();
                return IntPtr.Zero;
            }

            if (msg == CallbackMessage)
            {
                switch ((uint)(lParam.ToInt64() & 0xFFFF))
                {
                    case WM_LBUTTONDBLCLK:
                        Run(_onDoubleClick);
                        break;
                    case WM_CONTEXTMENU:
                        long anchor = wParam.ToInt64();
                        ShowMenu((short)(anchor & 0xFFFF), (short)((anchor >> 16) & 0xFFFF));
                        break;
                }
                return IntPtr.Zero;
            }

            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private void ShowMenu(int x, int y)
        {
            IntPtr menu = NativeMethods.CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                for (int i = 0; i < _menu.Count; i++)
                {
                    if (_menu[i] is { } item)
                        NativeMethods.AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)(i + 1), item.Text);
                    else
                        NativeMethods.AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                }

                // Without the window in the foreground the menu does not close when the user
                // clicks elsewhere; the WM_NULL afterwards lets the next click on the icon open
                // it again at the first try. Both are what TrackPopupMenu's documentation asks of
                // a notification-area menu.
                NativeMethods.SetForegroundWindow(_hwnd);
                uint align = NativeMethods.GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
                int command = NativeMethods.TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | align,
                                                             x, y, _hwnd, IntPtr.Zero);
                NativeMethods.PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

                if (command > 0 && command <= _menu.Count && _menu[command - 1] is { } chosen)
                    Run(chosen.Invoke);
            }
            finally
            {
                NativeMethods.DestroyMenu(menu);
            }
        }

        // An exception escaping a window procedure would take the process down with it.
        private static void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) { DebugLogger.Log($"[Tray] {ex.GetType().Name}: {ex.Message}"); }
        }

        // ── Win32 ────────────────────────────────────────────────────────────

        private const uint WM_NULL          = 0x0000;
        private const uint WM_CONTEXTMENU   = 0x007B;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_APP           = 0x8000;

        private const uint WS_POPUP         = 0x80000000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;

        private const uint NIM_ADD        = 0x0;
        private const uint NIM_MODIFY     = 0x1;
        private const uint NIM_DELETE     = 0x2;
        private const uint NIM_SETVERSION = 0x4;
        private const uint NIF_MESSAGE    = 0x01;
        private const uint NIF_ICON       = 0x02;
        private const uint NIF_TIP        = 0x04;
        private const uint NIF_SHOWTIP    = 0x80;
        private const uint NOTIFYICON_VERSION_4 = 4;

        private const uint IMAGE_ICON      = 1;
        private const uint LR_LOADFROMFILE = 0x10;

        private const uint MF_STRING    = 0x0000;
        private const uint MF_SEPARATOR = 0x0800;

        private const uint TPM_LEFTALIGN   = 0x0000;
        private const uint TPM_RIGHTALIGN  = 0x0008;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_NONOTIFY    = 0x0080;
        private const uint TPM_RETURNCMD   = 0x0100;

        private const int SM_MENUDROPALIGNMENT = 40;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;   // union with uTimeout
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        private static class NativeMethods
        {
            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
                                                        int x, int y, int nWidth, int nHeight,
                                                        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
            [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string lpString);
            [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);

            [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);
            [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr hMenu);
            [DllImport("user32.dll")]
            public static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);
            [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? lpModuleName);
        }
    }
}
