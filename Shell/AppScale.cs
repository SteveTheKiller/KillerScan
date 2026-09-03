using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerScan.Shell
{
    // App-wide accessibility size, ported from KillerNotes and KillerPDF: a LayoutTransform
    // scale on ScaleHost (the scan toolbar + devices pane, MainWindow.xaml) grows or shrinks
    // the app content crisply - LayoutTransform reflows and re-rasterizes text rather than
    // bitmap-stretching it, which RenderTransform would. The title bar and footer stay a fixed
    // size, so the wordmark you scroll to drive this (MainWindow.xaml, LogoBar) never moves.
    // Persisted app-wide ("AppScale").
    //
    // Unlike the siblings this needs no width bookkeeping. KillerNotes and KillerPDF both keep
    // a sidebar column in the UNSCALED outer grid, so every saved width has to be converted
    // across a scale change. KillerScan's DataGrid lives entirely inside ScaleHost, so its
    // saved column widths (MainWindow.xaml.cs, "GridColumns") are logical px measured inside
    // the transform and stay correct at any scale.
    public partial class MainWindow
    {
        private double _appScale = 1.0;
        private const double AppScaleMin = 0.7, AppScaleMax = 2.5, AppScaleStep = 0.02;

        private void InitAppScale()
        {
            if (double.TryParse(App.GetSetting("AppScale"), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out double s))
                ApplyAppScale(s);
        }

        // Roll the wheel over the wordmark: one small step per notch (fine-grained, no big jumps).
        private void LogoBar_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ApplyAppScale(_appScale + (e.Delta > 0 ? AppScaleStep : -AppScaleStep), persist: true);
            e.Handled = true;
        }

        // The wordmark is marked IsHitTestVisibleInChrome (MainWindow.xaml) so the scroll wheel
        // reaches it for the zoom above - but that also takes it out of WindowChrome's native
        // caption, so window drag and double-click-maximize are restored here by hand.
        private void LogoBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeBtn_Click(this, new RoutedEventArgs());   // WindowChrome.cs
                e.Handled = true;
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
                DragMove();
        }

        internal void ApplyAppScale(double scale, bool persist = false)
        {
            scale = Math.Round(Math.Max(AppScaleMin, Math.Min(AppScaleMax, scale)), 3);
            _appScale = scale;
            _watchWindow?.ApplyScale(scale);
            _diagnosticsWindow?.ApplyScale(scale);
            ScaleHost.LayoutTransform = scale == 1.0
                ? Transform.Identity
                : new ScaleTransform(scale, scale);

            // The window runs Display + ClearType, which pixel-snaps glyphs and color-fringes
            // them for maximum crispness at 1:1. Under a fractional scale (0.96, 1.12...) those
            // snapped stems land on partial device pixels and the whole table goes soft - worse
            // here than it would be elsewhere because the app forces software rendering
            // (App.xaml.cs) and the devices pane carries a DropShadowEffect, both of which
            // resample. Ideal + Grayscale positions glyphs at sub-pixel precision instead and
            // stays smooth at any scale. Same trade KillerPDF makes on its zoomed page surface
            // (MainWindow.xaml, PageContentGrid). At exactly 1.0 we hand control back to the
            // window so the default, and by far the most common, case keeps its crisp text.
            if (scale == 1.0)
            {
                ScaleHost.ClearValue(TextOptions.TextFormattingModeProperty);
                ScaleHost.ClearValue(TextOptions.TextRenderingModeProperty);
            }
            else
            {
                TextOptions.SetTextFormattingMode(ScaleHost, TextFormattingMode.Ideal);
                TextOptions.SetTextRenderingMode(ScaleHost, TextRenderingMode.Grayscale);
            }
            if (persist)
            {
                App.SetSetting("AppScale", scale.ToString("0.###", CultureInfo.InvariantCulture));
                ShowScaleReadout(scale);
            }
        }

        // The readout is transient. Every wheel notch rewrites it and restarts the hold timer,
        // so the footer carries it while you are zooming and gives the line back a beat after
        // you stop. Whatever was showing before the first notch of a burst is snapshotted and
        // put back - but only if the readout is still the text on screen, so a status written
        // in the meantime (a scan finishing, an export) is never overwritten by a stale one.
        //
        // Normal priority rather than the DispatcherTimer default of Background, so a busy
        // scan cannot leave the readout parked on the footer.
        private System.Windows.Threading.DispatcherTimer? _appScaleHide;
        private string _appScaleStatusWas = string.Empty;
        private string _appScaleReadout   = string.Empty;

        private void ShowScaleReadout(double scale)
        {
            if (_appScaleHide is null)
            {
                _appScaleHide = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Normal)
                    { Interval = TimeSpan.FromSeconds(5) };
                _appScaleHide.Tick += (_, _) =>
                {
                    _appScaleHide!.Stop();
                    if (StatusText.Text == _appScaleReadout) StatusText.Text = _appScaleStatusWas;
                };
            }

            // Only the first notch of a burst snapshots; the rest are our own readout.
            if (!_appScaleHide.IsEnabled) _appScaleStatusWas = StatusText.Text;
            _appScaleHide.Stop();

            _appScaleReadout = string.Format(Loc("Str_St_AppSize"), (int)Math.Round(scale * 100));
            StatusText.Text  = _appScaleReadout;
            _appScaleHide.Start();
        }
    }
}
