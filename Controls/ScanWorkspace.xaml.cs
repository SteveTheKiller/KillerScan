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
    public sealed class ScanDeviceActionEventArgs(NetworkDevice device, string action, bool beside) : EventArgs
    {
        public NetworkDevice Device { get; } = device;
        public string Action { get; } = action;
        public bool Beside { get; } = beside;
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
        internal ScanSession Session => _active;
        public NetworkDevice? SelectedDevice => GetSelectedDevice();
        public string Targets { get => SubnetInput.Text; set => SubnetInput.Text = value; }
        public bool IsScanning => _active.IsScanning || _rescanCts != null;
        public string View => _showTopology ? "topology" : _showServices ? "services" : "devices";
        public string Status => StatusText.Text;
        public double Progress => ScanProgress.Value;
        public bool IsProgressVisible => ScanProgress.Visibility == Visibility.Visible;
        public ScanWorkspace(string initialTarget = "", bool demo = false)
        {
            DemoMode = demo;
            InitializeComponent();
            StatusDescriptor.AddValueChanged(StatusText, NotifyFooterChanged);
            ProgressDescriptor.AddValueChanged(ScanProgress, NotifyFooterChanged);
            ProgressVisibilityDescriptor.AddValueChanged(ScanProgress, NotifyFooterChanged);
            PopulateNetworkInfo();
            if (!string.IsNullOrWhiteSpace(initialTarget)) SubnetInput.Text = initialTarget;
            _active = new ScanSession(SubnetInput.Text)
            { Status = string.Format(Loc("Str_Status_Ready"), OuiLookup.Count.ToString("N0")) };
            WireSession(_active);
            ActivateSession();
            RestoreColumnLayout();
            AddWorkspaceDeviceMenus();
            ServicesGrid.SelectionChanged += (_, _) =>
            {
                if (ServicesGrid.SelectedItem is ServiceRow row)
                    ResultsGrid.SelectedItem = _active.Devices.FirstOrDefault(device => device.IpAddress == row.IpAddress);
            };
            ServicesGrid.ContextMenu = ResultsGrid.ContextMenu;
            ServicesGrid.ContextMenuOpening += ResultsGrid_ContextMenuOpening;
            SizeChanged += (_, _) =>
            {
                SubnetInput.Width = Math.Max(40, Math.Min(ActualWidth < 1100 ? 180 : 220, ActualWidth - 28));
            };
            SubnetInput.TextChanged += (_, _) => { _active.SubnetText = Targets; StateChanged?.Invoke(this, EventArgs.Empty); };
            SubnetInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Scan(); e.Handled = true; } };
            Loaded += (_, _) =>
            {
                if (TryFindResource("GrainTileBrush") is ImageBrush grain) GrainBrush.ImageSource = grain.ImageSource;
            };
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
        public void FocusTargets() { SubnetInput.Focus(); SubnetInput.SelectAll(); }
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
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        public void Export() => ExportButton_Click(ExportButton, new RoutedEventArgs());
        public void RefreshLocale()
        {
            _filteredView?.Refresh();
            RefreshDeviceCount();
            UpdateDeepScanButton();
            ScanBtn.Content = Loc(_active.IsScanning ? "Str_Btn_Stop" : "Str_Btn_Scan");
            PaneTitle.Text = Loc(_showTopology ? "Str_Topology_Title" : _showServices ? "Str_Services_Title" : "Str_DiscoveredDevices");
            if (_showTopology) RefreshTopology();
            if (_showServices) RefreshServices();
        }
        public void ApplyScale(double scale) => LayoutTransform = new ScaleTransform(scale, scale);
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StatusDescriptor.RemoveValueChanged(StatusText, NotifyFooterChanged);
            ProgressDescriptor.RemoveValueChanged(ScanProgress, NotifyFooterChanged);
            ProgressVisibilityDescriptor.RemoveValueChanged(ScanProgress, NotifyFooterChanged);
            Stop();
            SaveColumnLayout();
        }
        internal void RunProfile(ScanProfile profile)
        {
            if (_disposed || IsScanning) return;
            Targets = profile.Target;
            _runDeepAfterScan = profile.DeepScanAfter;
            Scan();
        }
        internal void LoadSnapshot(string target, IEnumerable<NetworkDevice> devices)
        {
            if (_disposed || IsScanning) return;
            Targets = target;
            _active.ScannedSubnet = target;
            _active.Devices.Clear();
            foreach (var device in devices) _active.Devices.Add(device);
            RefreshDeviceCount();
            UpdateDeepScanButton();
            ExportButton.IsEnabled = _active.Devices.Count > 0;
        }
        private void RaiseDeviceAction(string action, bool beside = false)
        {
            var device = SelectedDevice;
            if (device != null) DeviceAction?.Invoke(this, new ScanDeviceActionEventArgs(device, action,
                beside));
        }
        private void HeaderStrip_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Border border) return;
            double w = border.ActualWidth, h = border.ActualHeight;
            if (w <= 0 || h <= 0) { border.Clip = null; return; }
            double r = Math.Min(6, Math.Min(w / 2, h));
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(0, h), true, true);
                context.LineTo(new Point(0, r), true, false);
                context.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(w - r, 0), true, false);
                context.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(w, h), true, false);
            }
            geometry.Freeze();
            border.Clip = geometry;
        }
    }
}
