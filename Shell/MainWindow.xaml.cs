using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using KillerScan.Controls;
using KillerScan.Features.About;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow : Window
    {
        private static bool DemoMode => DemoData.Enabled;
        private readonly StackPanel _portableBadge = null!;
        private readonly ImageBrush _grainBrush = null!;
        private readonly string? _startupScanTarget;
        private readonly bool _startScanOnLoad;
        private bool _closeFaded;

        public MainWindow(string? startupScanTarget = null, bool startScanOnLoad = false)
        {
            _startupScanTarget = startupScanTarget;
            _startScanOnLoad = startScanOnLoad;
            InitializeComponent();
            // Family standard: rail flyouts hug the results pane's lower-left corner. That is
            // inside the window, above the footer, and just to the right of the icon rail.
            FlyoutPlacement.UsePane(DevicesPane, RootGrid);
            _portableBadge = (StackPanel)FindName("PortableBadge")!;
            _grainBrush    = (ImageBrush)FindName("GrainBrush")!;

            OuiLookup.Load();
            DeviceOverrides.Load();
            DeviceLogins.Load();
            DevicePreferences.Load();
            ScanHistory.Load();
            ScanProfiles.Load();

            _about = new AboutController(this);

            VersionLabel.Text = $"v{AppInfo.Version}";

            InitializeWorkspace();
            InitSidebar();

            RestoreWindowPlacement();                               // window size/position from previous run
            InitAppScale();                                         // AppScale.cs (restore app-wide size)

            ApplyGrainTexture();                                    // WindowChrome.cs
            SourceInitialized += MainWindow_SourceInitialized;      // WindowChrome.cs

            Loaded += (_, _) =>
            {
                if (App.IsPortable() && !DemoMode)
                    _portableBadge.Visibility = Visibility.Visible;
                UpdateThemeSwatchSelection();                       // Theme.cs
                UpdateAccentStrip(animate: false);                  // Theme.cs
                ApplyFlatChrome();                                  // Theme.cs (98SE drops the shadow)
                FadeInContent();                                    // WindowChrome.cs
                if (_startScanOnLoad && !DemoMode)
                    Dispatcher.BeginInvoke(new Action(() => ActiveScan?.Scan()),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
        }

        private NetworkDevice? GetSelectedDevice() => _scanWorkspace?.SelectedDevice;

        // Services moved from the icon rail to the workspace toolbar, so the selected view is
        // lit by UpdateWorkspaceNavigation along with Scan, Topology, Keep Alive, and Terminal.
        private void UpdateWorkspaceRail() => UpdateWorkspaceNavigation();

        private void ServicesButton_Click(object sender, RoutedEventArgs e)
        {
            var scan = _scanWorkspace;
            if (scan == null) return;
            ShowScanView(ActiveScan != null && scan.View == "services" ? "devices" : "services");
        }

        private void TopologyButton_Click(object sender, RoutedEventArgs e)
        {
            var scan = _scanWorkspace;
            if (scan == null) return;
            ShowScanView(ActiveScan != null && scan.View == "topology" ? "devices" : "topology");
        }

        // Portable-mode install button
        private void Install_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ConfirmDialog { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            // An all-users install can still be refused at the UAC prompt; only hide the badge
            // once the install actually happened, otherwise the app keeps running as portable.
            if (!App.InstallAndRelaunch(wantDesktop: true, allUsers: dlg.AllUsers))
            {
                StatusText.Text = Loc("Str_St_InstallCanceled");
                return;
            }
            _portableBadge.Visibility = Visibility.Collapsed;
        }

        // Footer version number -> About overlay (About.cs).
        private void VersionLabel_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ShowAboutOverlay();
        }

        // ============================================================
        // Window placement persistence ("WindowPlacement" = left,top,w,h,max).
        // A maximized (or minimized) close saves RestoreBounds, so the
        // pre-maximize size comes back. Restore sanity-checks that the saved
        // rect still lands on the current virtual desktop (monitors change).
        // ============================================================

        private void SaveWindowPlacement()
        {
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                bool max = WindowState == WindowState.Maximized;
                Rect r = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                if (r.IsEmpty || r.Width < 1 || r.Height < 1 ||
                    double.IsNaN(r.X) || double.IsNaN(r.Y)) return;
                App.SetSetting("WindowPlacement", string.Join(",",
                    r.X.ToString("0.##", inv), r.Y.ToString("0.##", inv),
                    r.Width.ToString("0.##", inv), r.Height.ToString("0.##", inv),
                    max ? "1" : "0"));
            }
            catch { /* best-effort */ }
        }

        private void RestoreWindowPlacement()
        {
            string? s = App.GetSetting("WindowPlacement");
            if (string.IsNullOrWhiteSpace(s)) return;
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string[] f = s!.Split(',');
                if (f.Length != 5) return;
                if (!double.TryParse(f[0], System.Globalization.NumberStyles.Float, inv, out double left) ||
                    !double.TryParse(f[1], System.Globalization.NumberStyles.Float, inv, out double top)  ||
                    !double.TryParse(f[2], System.Globalization.NumberStyles.Float, inv, out double w)    ||
                    !double.TryParse(f[3], System.Globalization.NumberStyles.Float, inv, out double h))
                    return;
                w = Math.Max(MinWidth, w);
                h = Math.Max(MinHeight, h);

                // Keep at least a grabbable sliver of the title bar on-screen.
                double vl = SystemParameters.VirtualScreenLeft;
                double vt = SystemParameters.VirtualScreenTop;
                double vr = vl + SystemParameters.VirtualScreenWidth;
                double vb = vt + SystemParameters.VirtualScreenHeight;
                if (left + w < vl + 40 || left > vr - 40 || top < vt - 8 || top > vb - 40) return;

                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left; Top = top; Width = w; Height = h;
                if (f[4] == "1") WindowState = WindowState.Maximized;
            }
            catch { /* best-effort */ }
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveWindowPlacement();
            DisposeWorkspace();
            base.OnClosed(e);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (Anim.FadeOutAndClose(this, ref _closeFaded))
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }
    }
}
