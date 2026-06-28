using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace KillerScan
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
            ApplyWindowCorners(rounded: WindowState == WindowState.Normal);
        }

        // ---- Windows 11 rounded corners (DWMWA_WINDOW_CORNER_PREFERENCE = 33) ----
        // No-op on Windows 10 and earlier; the OS draws the drop shadow for us either way.

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND      = 2;

        private void ApplyWindowCorners(bool rounded)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int pref = rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { /* pre-Win11: no rounded-corner API */ }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            // Square the corners when maximized (flush to screen edges), round when floating.
            ApplyWindowCorners(rounded: WindowState == WindowState.Normal);
            // Maximize glyph toggles to a restore glyph when maximized (matches KillerPDF).
            if (MaximizeBtn != null)
                MaximizeBtn.Content = WindowState == WindowState.Maximized ? "" : "";
        }

        // ---- Content fade-in on open ----

        private void FadeInContent()
        {
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
                new Duration(TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };
            RootGrid.BeginAnimation(OpacityProperty, fade);
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
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
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        // ---- Custom bottom-right resize grip (the WPF CanResizeWithGrip dots fall out in the
        // transparent shadow margin, so we draw our own at the content corner and forward the
        // resize to Windows). Ported from KillerPDF. ----
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private void ResizeGrip_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (WindowState != WindowState.Normal) return;
            e.Handled = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTBOTTOMRIGHT), IntPtr.Zero);
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
