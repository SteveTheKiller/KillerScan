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
        // active session's collection/state live in Tabs.cs (_devices, _scannedSubnet, ...).
        private readonly ObservableCollection<ScanSession> _sessions = [];
        private ScanSession _active = null!;
        private ICollectionView? _filteredView;
        private StackPanel _portableBadge = null!;
        private ImageBrush _grainBrush = null!;

        public MainWindow()
        {
            InitializeComponent();
            _portableBadge = (StackPanel)FindName("PortableBadge")!;
            _grainBrush    = (ImageBrush)FindName("GrainBrush")!;

            OuiLookup.Load();
            DeviceOverrides.Load();

            var ver = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.5.0";
            VersionLabel.Text = $"v{ver}";

            PopulateNetworkInfo();                                   // NetworkInfo.cs (sets SubnetInput.Text)

            // First tab, seeded with the detected subnet.
            var first = new ScanSession(SubnetInput.Text)
            {
                Status = $"Ready -- {OuiLookup.Count:N0} OUI vendors loaded"
            };
            WireSession(first);                                     // Scanning.cs
            _sessions.Add(first);
            ActivateSession(first);                                 // Tabs.cs (binds grid to the session)

            ApplyGrainTexture();                                    // WindowChrome.cs
            SourceInitialized += MainWindow_SourceInitialized;      // WindowChrome.cs

            Loaded += (_, _) =>
            {
                if (App.IsPortable())
                    _portableBadge.Visibility = Visibility.Visible;
                UpdateThemeSwatchSelection();                       // Theme.cs
                UpdateAccentSwatches();                             // Theme.cs
                FadeInContent();                                    // WindowChrome.cs
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
    }
}
