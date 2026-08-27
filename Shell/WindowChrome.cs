using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using KillerScan.Controls;

namespace KillerScan.Shell
{
    // UI shell: custom window chrome - the maximize-respects-taskbar fix that a
    // WindowStyle=None window needs, the caption buttons, Win11 rounded corners,
    // the content fade-in, and the film-grain texture.
    public partial class MainWindow
    {
        // ---- Maximize-respects-taskbar (WindowStyle=None needs WM_GETMINMAXINFO) ----

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            SyncWindowCorners();
            ApplyThemeBorder(this);
        }

        // ---- Windows 11 rounded corners (DWMWA_WINDOW_CORNER_PREFERENCE = 33) ----
        // No-op on Windows 10 and earlier; the OS draws the drop shadow for us either way.

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND      = 2;
        private const int DWMWA_BORDER_COLOR = 34;

        /// <summary>Tints the Win11 DWM frame border to the theme's WindowEdgeBrush first,
        /// matching KillerNotes, then falls back through AppBorderBrush and PaneBorderBrush.
        /// follows the palette instead of staying system gray. Call at SourceInitialized
        /// and again after every theme change. (Family standard, ported from KillerFind.)</summary>
        internal static void ApplyThemeBorder(Window w)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                object? candidate = Application.Current.TryFindResource("WindowEdgeBrush");
                // KillerScan keeps a transparent WindowEdgeBrush in Defaults so the WPF edge is
                // absent on flat themes. Unlike KillerNotes, it does not republish that key from
                // AppBorderBrush. Therefore transparent means "no DWM edge" only for 98SE;
                // modern themes must fall through to their palette border.
                if (Services.ThemeManager.Current != Services.Theme.SE98 &&
                    candidate is System.Windows.Media.SolidColorBrush edge && edge.Color.A == 0)
                    candidate = null;
                candidate ??= Application.Current.TryFindResource("AppBorderBrush")
                           ?? Application.Current.TryFindResource("PaneBorderBrush");

                if (candidate is System.Windows.Media.SolidColorBrush b)
                {
                    // Transparent means no extra DWM keyline outside the WPF sizing frame.
                    int colorref = b.Color.A == 0
                        ? unchecked((int)0xFFFFFFFE)
                        : b.Color.R | (b.Color.G << 8) | (b.Color.B << 16);
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
                }
            }
            catch { /* pre-Win11: attribute unsupported */ }
        }

        // Last preference actually pushed to DWM, so the WM_WINDOWPOSCHANGED path can run on every
        // move without hammering the API. -1 = nothing set yet.
        private int _cornerPref = -1;

        private void ApplyWindowCorners(bool rounded)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int pref = rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
                if (pref == _cornerPref) return;
                _cornerPref = pref;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { /* pre-Win11: no rounded-corner API */ }
        }

        /// <summary>
        /// Round only when the window is genuinely floating. Maximized is the easy half; SNAPPED is
        /// the one that got missed, because a snapped window is still WindowState.Normal - Windows
        /// resizes it and never raises a state change - so OnStateChanged alone left a half-screen
        /// window with rounded corners against the screen edge. Driven from WM_WINDOWPOSCHANGED as
        /// well, which is the only message a snap does raise.
        /// </summary>
        private void SyncWindowCorners() =>
            ApplyWindowCorners(rounded: WindowState == WindowState.Normal
                                        && !IsEdgeSnapped()
                                        // 98SE squares even a floating window. Windows 98 had no
                                        // rounded windows, and the DWM corner preference is the one
                                        // rounding a palette cannot reach - WindowCornerRadius only
                                        // governs what WPF draws inside the frame.
                                        && Services.ThemeManager.Current != Services.Theme.SE98);

        /// <summary>
        /// True when the window sits flush against two or more edges of its monitor's work area,
        /// which every snap layout does: a half fills three edges, a quarter two. A floating window
        /// touches none unless it has been dragged into a corner by hand, and squaring it there is
        /// the right answer anyway. The 2px tolerance absorbs the invisible resize border DWM
        /// includes in the window rect.
        /// </summary>
        private bool IsEdgeSnapped()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return false;
                if (!GetWindowRect(hwnd, out RECT r)) return false;

                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero) return false;
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(monitor, ref info)) return false;

                RECT work = info.rcWork;
                const int tol = 2;
                int flush = 0;
                if (Math.Abs(r.left   - work.left)   <= tol) flush++;
                if (Math.Abs(r.top    - work.top)    <= tol) flush++;
                if (Math.Abs(r.right  - work.right)  <= tol) flush++;
                if (Math.Abs(r.bottom - work.bottom) <= tol) flush++;
                return flush >= 2;
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            // Square the corners when maximized (flush to screen edges), round when floating.
            SyncWindowCorners();
            // Maximize glyph toggles to a restore glyph when maximized (matches KillerPDF).
            MaximizeBtn?.Content = WindowState == WindowState.Maximized ? "" : "";
        }

        // ---- Content fade-in on open ----

        private void FadeInContent() => Anim.FadeIn(RootGrid);

        private const int WM_GETMINMAXINFO    = 0x0024;
        private const int WM_ERASEBKGND       = 0x0014;
        private const int WM_WINDOWPOSCHANGED = 0x0047;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_ERASEBKGND)
            {
                // KillerPDF's anti-flash trick: WPF paints the whole client area itself, so
                // let nothing erase the background to a flat fill during a resize - that
                // erase is the white flash. Claim the message and report success.
                handled = true;
                return new IntPtr(1);
            }
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            // A snap resizes the window without ever changing WindowState, so this is the only
            // notification that it happened. NOT marked handled - WPF needs it too.
            if (msg == WM_WINDOWPOSCHANGED)
                SyncWindowCorners();
            // Right-click on the caption / Alt+Space: show our themed menu instead of Windows'
            // stock white one, which is a Win32 HMENU and cannot be styled (SystemMenu.cs).
            if (TryHandleSystemMenu(msg, wParam, lParam))
            {
                handled = true;
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                GetMonitorInfo(monitor, ref info);
                RECT work = info.rcWork;
                RECT mon = info.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(work.left - mon.left);
                mmi.ptMaxPosition.y = Math.Abs(work.top - mon.top);
                mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
                mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
                mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;

                // ptMinTrackSize MUST be set here too - this handler marks the message handled,
                // and WM_GETMINMAXINFO is exactly how WPF enforces MinWidth/MinHeight during a
                // drag-resize. Without it the window drags below its minimum and the content is
                // CLIPPED rather than resized. Device pixels, so scale the DIP values.
                // (2026-07-30. KillerShell always had this; the kit did not.)
                var src = PresentationSource.FromVisual(this);
                double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                if (!double.IsNaN(MinWidth) && MinWidth > 0)
                    mmi.ptMinTrackSize.x = (int)Math.Ceiling(MinWidth * sx);
                if (!double.IsNaN(MinHeight) && MinHeight > 0)
                    mmi.ptMinTrackSize.y = (int)Math.Ceiling(MinHeight * sy);

                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }

        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        // ---- Custom bottom-right resize grip (the WPF CanResizeWithGrip dots fall out in the
        // transparent shadow margin, so we draw our own at the content corner and forward the
        // resize to Windows). Ported from KillerPDF. ----
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTBOTTOMRIGHT = 17;
        private const int HTCAPTION = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private void ResizeGrip_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (WindowState != WindowState.Normal) return;
            e.Handled = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTBOTTOMRIGHT), IntPtr.Zero);
        }

        // Lets the scan toolbar act like the title bar for dragging: interactive controls
        // (subnet box, Scan button) handle their own clicks, so only clicks on the bar's empty
        // space and passive labels bubble up here and forward a native caption drag. Native
        // HTCAPTION also gives correct restore-from-maximized-and-drag behavior for free.
        private void Toolbar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
            e.Handled = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // ---- Caption buttons (drag + double-click-maximize are native via WindowChrome) ----

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
            => Close();

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        // Titlebar wordmark opens the website (same rule as KillerPDF/KillerFind).
        private void Wordmark_Click(object sender, MouseButtonEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://scan.killertools.net") { UseShellExecute = true });
            e.Handled = true;
        }

        // ---- Film grain ----
        // Matched to KillerPDF: a mix of bright AND dark specks (~33% density) so the
        // texture reads on both dark and light themes. One bitmap, shared across the
        // results pane and the chrome bars. Same seed = identical pattern every run.

        private void ApplyGrainTexture()
        {
            const int size = 256;
            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new byte[size * size * 4]; // start fully transparent
            var rng = new Random(1337);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (rng.Next(3) != 0) continue;        // ~33% pixel density
                bool bright = rng.Next(2) == 0;         // half bright, half dark
                byte v = bright ? (byte)rng.Next(190, 255) : (byte)rng.Next(0, 50);
                byte a = (byte)rng.Next(35, 95);        // alpha for subtlety
                pixels[i]     = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = a;
            }
            bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);

            _grainBrush.ImageSource = bmp;
            if (FindName("TitleGrainBrush")   is ImageBrush tg) tg.ImageSource = bmp;
            if (FindName("ToolbarGrainBrush") is ImageBrush tb) tb.ImageSource = bmp;
            if (FindName("StatusGrainBrush")  is ImageBrush sg) sg.ImageSource = bmp;
            if (FindName("FlyoutGrainBrush")  is ImageBrush fg) fg.ImageSource = bmp;

            // The keyed resource brush is auto-frozen (unlike the x:Named ones above), so its
            // ImageSource can't be set in place. Swap in a fresh, frozen brush instead - DynamicResource
            // consumers (the context menus) re-resolve automatically.
            var grainTile = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new System.Windows.Rect(0, 0, size, size),
                Stretch = Stretch.None
            };
            grainTile.Freeze();
            Application.Current.Resources["GrainTileBrush"] = grainTile;
        }
    }
}
