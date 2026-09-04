using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerScan.Shell
{
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
            ApplyWorkspaceScale(scale);
            // The sidebar's width is stored in screen pixels so it keeps a constant on-screen
            // size, which means a zoom change has to recompute its logical width.
            RefreshSidebarWidth();
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
