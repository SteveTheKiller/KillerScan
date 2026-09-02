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
    // ============================================================
    // MainWindow - shell composition root.
    //
    // The window is intentionally thin: it holds shared fields and wires up the
    // pieces in the constructor. Everything else lives in focused partials in
    // this folder, split into a reusable UI shell and the scanner core:
    //
    //   UI shell:      WindowChrome.cs, Theme.cs, SystemMenu.cs, AppScale.cs,
    //                  Shortcuts.cs, About.cs
    //   Scanner core:  Scanning.cs, NetworkInfo.cs, Filtering.cs, Session.cs,
    //                  Rescan.cs, DeviceActions.cs, Export.cs, DemoMode.cs
    //
    // Everything in Shell/ is window code. The scan engine, the report writers
    // and the rest of the pure logic live in Services/, the controllers behind
    // an interface boundary in Features/, the dialogs and the shared animation
    // helper in Controls/, and the data types in Models/. Nothing in Features/
    // or Services/ reaches back into this folder.
    // ============================================================
    public partial class MainWindow : Window
    {
        // One session backs the one scan surface. A scan itself accepts several targets.
        private readonly ScanSession _active;
        private ICollectionView? _filteredView;
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

            _about = new AboutController(this);

            VersionLabel.Text = $"v{AppInfo.Version}";

            PopulateNetworkInfo();                                   // NetworkInfo.cs (sets SubnetInput.Text)
            if (!string.IsNullOrWhiteSpace(_startupScanTarget))
                SubnetInput.Text = _startupScanTarget;

            _active = new ScanSession(SubnetInput.Text ?? string.Empty)
            {
                Status = string.Format(Loc("Str_Status_Ready"), OuiLookup.Count.ToString("N0"))
            };
            WireSession(_active);                                   // Scanning.cs
            ActivateSession();                                      // Session.cs (binds grid to the session)

            RestoreWindowPlacement();                               // window size/position from previous run
            RestoreColumnLayout();                                  // column order/widths from previous run
            InitAppScale();                                         // AppScale.cs (restore app-wide size)

            ApplyGrainTexture();                                    // WindowChrome.cs
            SourceInitialized += MainWindow_SourceInitialized;      // WindowChrome.cs

            Loaded += (_, _) =>
            {
                if (App.IsPortable())
                    _portableBadge.Visibility = Visibility.Visible;
                UpdateThemeSwatchSelection();                       // Theme.cs
                UpdateAccentStrip(animate: false);                  // Theme.cs
                ApplyFlatChrome();                                  // Theme.cs (98SE drops the shadow)
                FadeInContent();                                    // WindowChrome.cs
                if (DemoMode) GenerateDemoScan();                   // DemoMode.cs (--demo: initial roll)
                else if (_startScanOnLoad)
                    Dispatcher.BeginInvoke(new Action(() => ScanBtn_Click(this, new RoutedEventArgs())),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
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
        // Column layout persistence (Software\KillerScan\Settings, "GridColumns").
        // Saves each column's display position always, and its width ONLY if the
        // user resized it (dragging converts the width to pixels). Untouched
        // columns keep their XAML sizing (Auto / star) so they still adapt to
        // content. Format: Name:DisplayIndex:Width|... ("-" = width untouched).
        // ============================================================

        private Dictionary<string, DataGridColumn> ColumnMap() => new()
        {
            ["ColIp"]     = ColIp,
            ["ColHost"]   = ColHost,
            ["ColMac"]    = ColMac,
            ["ColVendor"] = ColVendor,
            ["ColType"]   = ColType,
            ["ColPorts"]  = ColPorts,
        };

        private void SaveColumnLayout()
        {
            try
            {
                var parts = ColumnMap().Select(kv =>
                {
                    var c = kv.Value;
                    string w = c.Width.UnitType == DataGridLengthUnitType.Pixel
                        ? c.Width.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                        : "-";
                    return $"{kv.Key}:{c.DisplayIndex}:{w}";
                });
                App.SetSetting("GridColumns", string.Join("|", parts));
            }
            catch { /* best-effort */ }
        }

        private void RestoreColumnLayout()
        {
            string? s = App.GetSetting("GridColumns");
            if (string.IsNullOrWhiteSpace(s)) return;
            try
            {
                var map = ColumnMap();
                int count = ResultsGrid.Columns.Count;
                var order = new List<(DataGridColumn Col, int Idx)>();
                foreach (string part in s!.Split('|'))
                {
                    string[] f = part.Split(':');
                    if (f.Length != 3 || !map.TryGetValue(f[0], out var col)) continue;
                    if (int.TryParse(f[1], out int idx) && idx >= 0 && idx < count)
                        order.Add((col, idx));
                    if (f[2] != "-" &&
                        double.TryParse(f[2], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out double w) &&
                        w > 0)
                        col.Width = new DataGridLength(w);
                }
                // Apply positions in ascending target order so each assignment
                // lands the column at its final slot.
                foreach (var (col, idx) in order.OrderBy(o => o.Idx))
                    col.DisplayIndex = idx;
            }
            catch { /* best-effort */ }
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
            SaveColumnLayout();
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
