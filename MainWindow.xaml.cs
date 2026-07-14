using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan
{
    // ============================================================
    // MainWindow - shell composition root.
    //
    // The window is intentionally thin: it holds shared fields and wires up the
    // pieces in the constructor. Everything else lives in focused partials,
    // split into a reusable UI shell and the scanner core:
    //
    //   UI shell:      WindowChrome.cs, Theme.cs, Grain.cs, Install.cs
    //   Scanner core:  Scanning.cs, NetworkInfo.cs, Filtering.cs,
    //                  DeviceActions.cs, Export.cs
    //
    // The scan engine itself (NetworkScanner, OuiLookup, DeviceOverrides) lives
    // in Services/; the partials hold only the thin glue to the UI.
    // ============================================================
    public partial class MainWindow : Window
    {
        // Each tab is a ScanSession; _active is the one shown in the grid. Proxies for the
        // active session's collection/state live in Tabs.cs (ActiveDevices, ActiveSubnet, ...).
        private readonly ObservableCollection<ScanSession> _sessions = [];
        private ScanSession _active = null!;
        private ICollectionView? _filteredView;
        private readonly StackPanel _portableBadge = null!;
        private readonly ImageBrush _grainBrush = null!;

        public MainWindow()
        {
            InitializeComponent();
            _portableBadge = (StackPanel)FindName("PortableBadge")!;
            _grainBrush    = (ImageBrush)FindName("GrainBrush")!;

            OuiLookup.Load();
            DeviceOverrides.Load();

            var ver = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.5.2";
            VersionLabel.Text = $"v{ver}";

            PopulateNetworkInfo();                                   // NetworkInfo.cs (sets SubnetInput.Text)

            // First tab, seeded with the detected subnet.
            var first = new ScanSession(SubnetInput.Text)
            {
                Status = string.Format(Loc("Str_Status_Ready"), OuiLookup.Count.ToString("N0"))
            };
            WireSession(first);                                     // Scanning.cs
            _sessions.Add(first);
            ActivateSession(first);                                 // Tabs.cs (binds grid to the session)

            RestoreWindowPlacement();                               // window size/position from previous run
            RestoreColumnLayout();                                  // column order/widths from previous run

            ApplyGrainTexture();                                    // WindowChrome.cs
            SourceInitialized += MainWindow_SourceInitialized;      // WindowChrome.cs

            Loaded += (_, _) =>
            {
                if (App.IsPortable())
                    _portableBadge.Visibility = Visibility.Visible;
                UpdateThemeSwatchSelection();                       // Theme.cs
                UpdateAccentSwatches();                             // Theme.cs
                FadeInContent();                                    // WindowChrome.cs
                if (DemoMode) GenerateDemoScan();                   // DemoMode.cs (--demo: initial roll)
            };
        }

        // Portable-mode install button
        private void Install_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ConfirmDialog { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            _portableBadge.Visibility = Visibility.Collapsed;
            App.InstallAndRelaunch(wantDesktop: true);
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
    }
}
