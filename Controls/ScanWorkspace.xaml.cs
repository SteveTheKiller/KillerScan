using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public sealed class ScanDeviceActionEventArgs(NetworkDevice device, string action, bool beside, int? servicePort = null) : EventArgs
    {
        public NetworkDevice Device { get; } = device;
        public string Action { get; } = action;
        public bool Beside { get; } = beside;
        public int? ServicePort { get; } = servicePort;
    }

    public partial class ScanWorkspace : UserControl, IDisposable
    {
        private readonly ScanSession _active;
        private ICollectionView? _filteredView;
        private bool _disposed;
        private bool _runDeepAfterScan;
        private readonly TextBlock StatusText = new();
        private readonly TextBlock PaneTitle = new();
        private readonly ProgressBar ScanProgress = new() { Visibility = Visibility.Collapsed };
        private readonly Button TopologyButton = new();
        private readonly Button ServicesButton = new();
        public event EventHandler<ScanDeviceActionEventArgs>? DeviceAction;
        public event EventHandler? StateChanged;

        /// <summary>Raised once a finished scan has been written to the history store.</summary>
        public event EventHandler? HistoryRecorded;
        internal ScanSession Session => _active;
        internal ScanOptions Options => _active.Options;
        public NetworkDevice? SelectedDevice => GetSelectedDevice();
        public string Targets { get => SubnetInput.Text; set => SubnetInput.Text = value; }
        public bool IsScanning => _active.IsScanning || _rescanCts != null;

        /// <summary>Set when a scan runs to completion, cleared when one starts or is stopped.</summary>
        private bool _scanCompleted;

        /// <summary>
        /// What the status light shows. Deep scanning outranks scanning, because a deep pass runs
        /// after a normal one and is the thing actually happening.
        /// </summary>
        public ScanIndicator Indicator =>
            _rescanCts != null ? ScanIndicator.Deep
            : _active.IsScanning ? ScanIndicator.Scanning
            : _scanCompleted ? ScanIndicator.Complete
            : ScanIndicator.Idle;
        public string View => _showTopology ? "topology" : _showServices ? "services" : "devices";
        public string Status => StatusText.Text;
        /// <summary>The devices from the last scan, for the shell's status tooltip.</summary>
        internal IReadOnlyList<NetworkDevice> ScannedDevices => _active.Devices;
        public double Progress => ScanProgress.Value;
        public bool IsProgressVisible => ScanProgress.Visibility == Visibility.Visible;
        public ScanWorkspace(string initialTarget = "", bool demo = false)
            : this(initialTarget, demo, new NetworkScanner()) { }

        internal ScanWorkspace(string initialTarget, bool demo, NetworkScanner scanner)
        {
            DemoMode = demo;
            InitializeLocation();
            InitializeComponent();
            StatusDescriptor.AddValueChanged(StatusText, NotifyFooterChanged);
            ProgressDescriptor.AddValueChanged(ScanProgress, NotifyFooterChanged);
            ProgressVisibilityDescriptor.AddValueChanged(ScanProgress, NotifyFooterChanged);
            PopulateNetworkInfo();
            if (!string.IsNullOrWhiteSpace(initialTarget)) SubnetInput.Text = initialTarget;
            _active = new ScanSession(SubnetInput.Text, scanner)
            { Status = string.Format(Loc("Str_Status_Ready"), OuiLookup.Count.ToString("N0")) };
            WireSession(_active);
            ApplyLocationHooks();
            InitializeLocationSuggestions();
            ScanOptionsMenu.Attach(ScanOptionsButton, () => _active.Options,
                value => { if (!_disposed && !IsScanning) _active.Options = value; },
                new ScanOptionsMenu.AdapterHooks(() => AdapterChoice,
                    value => { if (!_disposed && !IsScanning) AdapterChoice = value; },
                    () => DemoMode ? [] : AvailableAdapters()));
            ActivateSession();
            StartVendorNoticeTimer();
            RestoreColumnLayout();
            FitMacColumn();
            // Themes and the interface font change the grid's font; the MAC column follows.
            // A named handler, so Dispose can remove it: the descriptor table would otherwise keep
            // every closed workspace alive.
            _fitMac = (_, _) => FitMacColumn();
            GridFontFamily.AddValueChanged(ResultsGrid, _fitMac);
            GridFontSize.AddValueChanged(ResultsGrid, _fitMac);
            Loaded += (_, _) => FitMacColumn();
            AddWorkspaceDeviceMenus();
            ServicesGrid.SelectionChanged += (_, _) =>
            {
                if (ServicesGrid.SelectedItem is ServiceRow row)
                    ResultsGrid.SelectedItem = _active.Devices.FirstOrDefault(device => device.IpAddress == row.IpAddress);
            };
            ScanToolbar.SizeChanged += (_, _) => FitToolbarInputs();
            FilterBox.IsVisibleChanged += (_, _) => FitToolbarInputs();
            DeepScanAllButton.IsVisibleChanged += (_, _) => FitToolbarInputs();
            // A language switch changes these buttons' widths without resizing the bar.
            ScanBtn.SizeChanged += (_, _) => FitToolbarInputs();
            ScanOptionsButton.SizeChanged += (_, _) => FitToolbarInputs();
            LocationButton.SizeChanged += (_, _) => FitToolbarInputs();
            DeepScanAllButton.SizeChanged += (_, _) => FitToolbarInputs();
            TopologyOrderButton.IsVisibleChanged += (_, _) => FitToolbarInputs();
            SubnetInput.TextChanged += (_, _) => { _active.SubnetText = Targets; StateChanged?.Invoke(this, EventArgs.Empty); };
            SubnetInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Scan(); e.Handled = true; } };
            ResultsGrid.PreviewKeyDown += TabOutOfGrid;
            ServicesGrid.PreviewKeyDown += TabOutOfGrid;
            ResultsGrid.GotKeyboardFocus += EnterGridCell;
            ServicesGrid.GotKeyboardFocus += EnterGridCell;
            Loaded += (_, _) => GrainTexture.Track(GrainBrush);
            if (demo) GenerateDemoScan();
        }
        private string Loc(string key) => TryFindResource(key) as string ?? key;
        private static readonly DependencyPropertyDescriptor StatusDescriptor =
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        private static readonly DependencyPropertyDescriptor ProgressDescriptor =
            DependencyPropertyDescriptor.FromProperty(ProgressBar.ValueProperty, typeof(ProgressBar));
        private static readonly DependencyPropertyDescriptor ProgressVisibilityDescriptor =
            DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(ProgressBar));
        private void NotifyFooterChanged(object? sender, EventArgs e)
        {
            if (!_disposed) StateChanged?.Invoke(this, EventArgs.Empty);
        }
        public void Scan() { if (!_disposed) ScanBtn_Click(this, new RoutedEventArgs()); }
        public void Stop() { _active.Cts?.Cancel(); _rescanCts?.Cancel(); }
        public void DeepScan() { if (!_disposed) DeepScanAll_Click(this, new RoutedEventArgs()); }
        private void FitToolbarInputs()
        {
            if (ScanToolbar.ActualWidth <= 0) return;
            var row = (Panel)SubnetInput.Parent;
            double fixedWidth = row.Children.OfType<FrameworkElement>()
                .Where(c => c != SubnetInput && c != FilterBox && c.Visibility == Visibility.Visible)
                .Sum(c => c.DesiredSize.Width);
            double available = Math.Max(0, ScanToolbar.ActualWidth - fixedWidth - SubnetInput.Margin.Left - SubnetInput.Margin.Right);
            bool filtering = FilterBox.Visibility == Visibility.Visible;
            double filterWidth = filtering ? Math.Min(150, Math.Max(60, (available - 100) / 2)) : 0;
            SubnetInput.Width = Math.Max(40, Math.Min(220, available - filterWidth - (filtering ? 6 : 0)));
            if (filtering) FilterBox.Width = filterWidth;
        }

        public void FocusTargets() { SubnetInput.Focus(); SubnetInput.SelectAll(); }
        internal FrameworkElement DetachToolbar()
        {
            ((Panel)ScanToolbar.Parent).Children.Remove(ScanToolbar);
            return ScanToolbar;
        }
        public void FocusFilter()
        {
            FilterBox.Visibility = Visibility.Visible;
            FilterInput.Focus();
            FilterInput.SelectAll();
        }
        private void FilterToggle_Click(object sender, RoutedEventArgs e)
        {
            if (FilterBox.Visibility == Visibility.Visible)
                CloseFilter();
            else FocusFilter();
        }
        private void CloseFilter()
        {
            ClearFilter();
            FilterBox.Visibility = Visibility.Collapsed;
            FilterToggleButton.Focus();
        }
        public void ClearFilter() => FilterInput.Clear();
        public void SetView(string view)
        {
            bool topology = string.Equals(view, "topology", StringComparison.OrdinalIgnoreCase);
            bool services = string.Equals(view, "services", StringComparison.OrdinalIgnoreCase);
            if (_showTopology != topology) TopologyButton_Click(this, new RoutedEventArgs());
            if (_showServices != services) ServicesButton_Click(this, new RoutedEventArgs());
            // Last word on the view's own chrome, so it is right however we arrived here.
            UpdateViewChrome();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        public void Export() => ExportButton_Click(ExportButton, new RoutedEventArgs());
        public void RefreshLocale()
        {
            RefreshColumnHeaders();
            _filteredView?.Refresh();
            RefreshDeviceCount();
            UpdateDeepScanButton();
            ScanBtn.Content = Loc(_active.IsScanning ? "Str_Btn_Stop" : "Str_Btn_Scan");
            PaneTitle.Text = Loc(_showTopology ? "Str_Topology_Title" : _showServices ? "Str_Services_Title" : "Str_DiscoveredDevices");
            if (_showTopology) RefreshTopology();
            if (_showServices) RefreshServices();
            UpdateLocationLabel();
            if (LocationSuggestion.Visibility == Visibility.Visible) RefreshLocationSuggestion();
        }
        // Grid columns sit outside the visual tree, so their DynamicResource headers keep the
        // language they were created in. They are restated on every language change.
        private void RefreshColumnHeaders()
        {
            ColIp.Header = Loc("Str_Col_Ip");
            ColHost.Header = Loc("Str_Col_Host");
            ColMac.Header = Loc("Str_Col_Mac");
            ColVendor.Header = Loc("Str_Col_Vendor");
            ColType.Header = Loc("Str_Col_Type");
            ColPorts.Header = Loc("Str_Col_Ports");
            string[] services = ["Str_Services_Service", "Str_Col_Ports", "Str_Col_DeviceName", "Str_Col_Ip", "Str_Col_Type"];
            for (int i = 0; i < services.Length && i < ServicesGrid.Columns.Count; i++) ServicesGrid.Columns[i].Header = Loc(services[i]);
        }

        public void ApplyScale(double scale) => LayoutTransform = new ScaleTransform(scale, scale);
        private static readonly DependencyPropertyDescriptor GridFontFamily = DependencyPropertyDescriptor.FromProperty(Control.FontFamilyProperty, typeof(DataGrid));
        private static readonly DependencyPropertyDescriptor GridFontSize = DependencyPropertyDescriptor.FromProperty(Control.FontSizeProperty, typeof(DataGrid));
        private EventHandler? _fitMac;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _viewRefreshTimer?.Stop();
            _vendorNoticeTimer?.Stop();
            StatusDescriptor.RemoveValueChanged(StatusText, NotifyFooterChanged);
            if (_fitMac != null)
            {
                GridFontFamily.RemoveValueChanged(ResultsGrid, _fitMac);
                GridFontSize.RemoveValueChanged(ResultsGrid, _fitMac);
            }
            ProgressDescriptor.RemoveValueChanged(ScanProgress, NotifyFooterChanged);
            ProgressVisibilityDescriptor.RemoveValueChanged(ScanProgress, NotifyFooterChanged);
            DisposeLocationSuggestions();
            Stop();
            SaveColumnLayout();
        }
        internal void RunProfile(ScanProfile profile)
        {
            if (_disposed || IsScanning) return;
            LoadProfile(profile);
            _runDeepAfterScan = profile.DeepScanAfter;
            Scan();
        }
        internal void LoadProfile(ScanProfile profile)
        {
            if (_disposed || IsScanning) return;
            Targets = profile.Target;
            _active.Options = profile.Options ?? new ScanOptions();
        }
        internal void LoadSnapshot(string target, IEnumerable<NetworkDevice> devices, DateTimeOffset? scannedAt = null)
        {
            if (_disposed || IsScanning) return;
            Targets = target;
            _active.ScannedSubnet = target;
            _active.ScannedTargets = target;
            _active.ScannedAt = scannedAt ?? DateTimeOffset.Now;
            _active.Completion = ScanCompletion.Complete;
            _active.Refresh = null;
            _active.Adapter = string.Empty;
            _active.Network = ScanNetwork.Unknown;
            _active.Devices.Clear();
            foreach (var device in devices) _active.Devices.Add(device);
            RefreshDeviceCount();
            UpdateDeepScanButton();
            UpdateExportButtonAvailability();
        }
        private static void EnterGridCell(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not DataGrid grid || !ReferenceEquals(e.NewFocus, grid) || grid.Items.Count == 0) return;
            if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed ||
                Mouse.MiddleButton == MouseButtonState.Pressed) return;
            grid.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                if (!grid.IsKeyboardFocused || grid.Items.Count == 0) return;
                var item = grid.CurrentCell.IsValid && grid.Items.Contains(grid.CurrentCell.Item)
                    ? grid.CurrentCell.Item : grid.SelectedItem ?? grid.Items[0];
                var column = grid.CurrentCell.IsValid && grid.CurrentCell.Column.Visibility == Visibility.Visible
                    ? grid.CurrentCell.Column
                    : grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).FirstOrDefault();
                if (column == null) return;
                grid.CurrentCell = new DataGridCellInfo(item, column);
                if (grid.SelectedItems.Count == 0) grid.SelectedItem = item;
                grid.ScrollIntoView(item, column);
                grid.UpdateLayout();
                DependencyObject? content = column.GetCellContent(item);
                while (content != null && content is not DataGridCell) content = VisualTreeHelper.GetParent(content);
                if (content is DataGridCell cell) cell.Focus();
            }));
        }

        /// <summary>
        /// Tab leaves a results grid in one step, as it leaves a list box, and the arrow keys
        /// still move between cells. The grid's own Tab handling visits every cell in the row first.
        /// </summary>
        internal static void TabOutOfGrid(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Tab || sender is not DataGrid grid) return;
            var modifiers = Keyboard.Modifiers;
            if ((modifiers & ~ModifierKeys.Shift) != ModifierKeys.None) return;
            var request = new TraversalRequest(modifiers == ModifierKeys.Shift
                ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next);
            for (int step = 0; step < 256 && grid.IsKeyboardFocusWithin; step++)
                if (Keyboard.FocusedElement is not UIElement focused || !focused.MoveFocus(request)) break;
            e.Handled = !grid.IsKeyboardFocusWithin;
        }

        private void RaiseDeviceAction(string action, bool beside = false)
        {
            if (_showServices && action is "Browser" or "Rdp" or "Ssh" or "SshAs")
            {
                RaiseServiceAction(action);
                return;
            }
            var device = SelectedDevice;
            if (device != null) DeviceAction?.Invoke(this, new ScanDeviceActionEventArgs(device, action,
                beside));
        }
    }
}
