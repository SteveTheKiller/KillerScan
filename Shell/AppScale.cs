using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// App-wide accessibility size, the family feature from KillerNotes and KillerPDF: roll the mouse
// wheel over the title-bar wordmark and everything under the title bar grows or shrinks in small
// steps. A LayoutTransform reflows and re-rasterizes text instead of stretching a bitmap, so the
// UI stays crisp at any size. The title bar itself stays fixed, so the wordmark being scrolled
// never moves under the cursor. Persisted app-wide through the ThemeManager settings hooks
// ("AppScale").
//
// MainWindow.xaml names:
//   LogoBar    the title-bar wordmark host, IsHitTestVisibleInChrome="True" so it gets the wheel,
//              wired to LogoBar_MouseWheel and LogoBar_MouseLeftButtonDown
//   ScaleHost  the element holding everything below the title bar (toolbar, sidebar, content,
//              footer) - the one LayoutTransform target
//   StatusText the footer status line that carries the "App size 120%" readout
//
// A document or canvas that has its own zoom (a PDF page, an image) should sit OUTSIDE ScaleHost
// or reset the transform for itself: app size and content zoom are two separate controls.
namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private double _appScale = 1.0;
        private double AppScaleMin = 0.7, AppScaleMax = 2.5, AppScaleStep = 0.02;
        private bool _appScaleSmoothText = true, _appScaleConfigured, _appScaleUnroundedLayout;

        /// <summary>Optional app hook: keep an app's own range, step, or ClearType text under scale.</summary>
        partial void ConfigureAppScale(ref double min, ref double max, ref double step, ref bool smoothText);

        /// <summary>Optional app hook: show the size readout somewhere other than StatusText.</summary>
        partial void ShowAppScaleReadout(double scale, ref bool handled);

        /// <summary>Optional app hook: set unroundedLayout true to turn layout rounding off on
        /// ScaleHost at fractional sizes (it comes back on at 100%). Rounded edges under a
        /// fractional scale can leave a one-pixel stripe between panes (KillerNotes at 108%).</summary>
        partial void ConfigureAppScaleLayout(ref bool unroundedLayout);

        private void EnsureAppScaleConfigured()
        {
            if (_appScaleConfigured) return;
            _appScaleConfigured = true;
            ConfigureAppScale(ref AppScaleMin, ref AppScaleMax, ref AppScaleStep, ref _appScaleSmoothText);
            ConfigureAppScaleLayout(ref _appScaleUnroundedLayout);
        }

        /// <summary>Call from the constructor after InitializeComponent to restore the saved size.</summary>
        private void InitAppScale()
        {
            EnsureAppScaleConfigured();
            if (double.TryParse(Services.ThemeManager.GetSetting("AppScale"), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out double saved))
                ApplyAppScale(saved);
        }

        // One small step per wheel notch: fine-grained, no big jumps.
        private void LogoBar_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            EnsureAppScaleConfigured();
            ApplyAppScale(_appScale + (e.Delta > 0 ? AppScaleStep : -AppScaleStep), persist: true);
            e.Handled = true;
        }

        // The wordmark is a client hit (for the wheel above), not caption, so drag and
        // double-click-maximize are restored here by hand. The drag is handed to Windows as a
        // caption press rather than DragMove: DragMove throws while maximized, and the native
        // path restores the window under the cursor and keeps following it.
        private void LogoBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeBtn_Click(this, new RoutedEventArgs());   // Chrome.cs
                e.Handled = true;
                return;
            }
            if (e.ButtonState != MouseButtonState.Pressed) return;
            e.Handled = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }

        /// <summary>Optional app hook: scale the app's own content instead of ScaleHost.</summary>
        partial void ApplyAppScaleTo(double scale, ref bool handled);

        internal void ApplyAppScale(double scale, bool persist = false)
        {
            EnsureAppScaleConfigured();
            scale = Math.Round(Math.Max(AppScaleMin, Math.Min(AppScaleMax, scale)), 3);
            _appScale = scale;

            // The window runs Display + ClearType, which pixel-snaps glyphs for maximum crispness at
            // 1:1. Under a fractional scale those snapped stems land on partial device pixels and
            // the text goes soft. Ideal + Grayscale positions glyphs at sub-pixel precision and
            // stays smooth at any size; at exactly 1.0 the window's own crisp setting comes back.
            // An app that keeps ClearType under scale opts out through ConfigureAppScale.
            if (scale == 1.0 || !_appScaleSmoothText)
            {
                ScaleHost.ClearValue(TextOptions.TextFormattingModeProperty);
                ScaleHost.ClearValue(TextOptions.TextRenderingModeProperty);
            }
            else
            {
                TextOptions.SetTextFormattingMode(ScaleHost, TextFormattingMode.Ideal);
                TextOptions.SetTextRenderingMode(ScaleHost, TextRenderingMode.Grayscale);
            }

            if (_appScaleUnroundedLayout)
            {
                if (scale == 1.0) ScaleHost.ClearValue(UseLayoutRoundingProperty);
                else ScaleHost.UseLayoutRounding = false;
            }

            // The app hook runs last, so whatever it sets wins over the kit defaults above. An app
            // whose scaled content is not one element (KillerScan's scan bar rides in the title bar)
            // implements ApplyAppScaleTo and sets handled; everyone else scales ScaleHost.
            bool handled = false;
            ApplyAppScaleTo(scale, ref handled);
            if (!handled) ScaleHost.LayoutTransform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);

            if (persist)
            {
                Services.ThemeManager.SetSetting("AppScale", scale.ToString("0.###", CultureInfo.InvariantCulture));
                ShowScaleReadout(scale);
            }
        }

        // The readout is transient. Every wheel notch rewrites it and restarts the hold timer, so
        // the footer carries it while you are sizing and gives the line back a beat after you stop.
        // Whatever was showing before the first notch of a burst is put back, but only if the
        // readout is still the text on screen, so a status written in the meantime is never
        // overwritten by a stale one. Normal priority rather than the DispatcherTimer default of
        // Background, so busy work cannot leave the readout parked on the footer.
        private DispatcherTimer? _appScaleHide;
        private string _appScaleStatusWas = string.Empty;
        private string _appScaleReadout = string.Empty;

        private void ShowScaleReadout(double scale)
        {
            bool handled = false;
            ShowAppScaleReadout(scale, ref handled);
            if (handled) return;

            if (_appScaleHide is null)
            {
                _appScaleHide = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(5) };
                _appScaleHide.Tick += (_, _) =>
                {
                    _appScaleHide!.Stop();
                    if (StatusText.Text == _appScaleReadout) StatusText.Text = _appScaleStatusWas;
                };
            }

            // Only the first notch of a burst snapshots; the rest are our own readout.
            if (!_appScaleHide.IsEnabled) _appScaleStatusWas = StatusText.Text;
            _appScaleHide.Stop();

            string format = TryFindResource("Str_St_AppSize") as string ?? "App size {0}%";
            _appScaleReadout = string.Format(CultureInfo.CurrentCulture, format, (int)Math.Round(scale * 100));
            StatusText.Text = _appScaleReadout;
            _appScaleHide.Start();
        }
    }
}
