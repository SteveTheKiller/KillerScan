using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KillerScan.Controls;
using KillerScan.Engine;
using KillerScan.Services;

// The scan tab reads and records everything through its own Location.
internal static class LocationWorkspaceTests
{
    private sealed class SiteScanner : NetworkScanner
    {
        internal TaskCompletionSource<bool>? Hold;
        public override async Task<List<NetworkDevice>> ScanSubnetAsync(string target, CancellationToken token,
            ScanOptions options, bool fullScan = true)
        {
            if (Hold != null) await Hold.Task;
            var device = new NetworkDevice { IpAddress = "192.168.1.10", MacAddress = "", DeviceType = "Unknown" };
            Completed?.Invoke(device);
            ReportDeviceFound(device);
            return [device];
        }
    }

    internal static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        bool demo = DemoData.Enabled;
        DemoData.Enabled = false;
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-location-workspace-" + Guid.NewGuid().ToString("N"));
        string originalRoot = LocationService.Root;
        try
        {
            var catalog = new LocationCatalog(root);
            catalog.Load("Unassigned");
            catalog.Create("Site A", out var infoA);
            catalog.Create("Site B", out var infoB);
            var a = catalog.Open(infoA!);
            var b = catalog.Open(infoB!);
            var device = new NetworkDevice { IpAddress = "192.168.1.10" };
            a.Devices.SetName(device, "Front desk");

            var scanner = new SiteScanner();
            using var workspace = new ScanWorkspace("192.168.1.10", false, scanner);
            Require(workspace.SetLocation(a), "The tab moves to Site A.");
            workspace.Scan();
            PumpUntil(() => !workspace.IsScanning && a.History.Entries.Count == 1);
            Require(workspace.Session.Devices.Single().Hostname == "Front desk", "Site A's saved name is applied to its scan.");
            Require(a.History.Entries.Count == 1 && a.Devices.HasBaseline && !a.Devices.IsTrusted(device),
                "The first scan in Site A records history and a baseline, and approves nothing.");
            Require(workspace.Status == string.Format((string)Application.Current.FindResource("Str_St_KnownBaseline"), 1),
                "The status says the baseline was set.");

            Require(workspace.SetLocation(b), "The tab moves to Site B.");
            workspace.Scan();
            PumpUntil(() => !workspace.IsScanning && b.History.Entries.Count == 1);
            Require(workspace.Session.Devices.Single().Hostname.Length == 0, "Site A's name never reaches the same IP in Site B.");
            Require(a.History.Entries.Count == 1 && b.History.Entries.Count == 1, "Each run is recorded only in its own Location.");

            scanner.Hold = new TaskCompletionSource<bool>();
            workspace.Scan();
            Require(workspace.IsScanning && !workspace.SetLocation(a), "The Location cannot change while a scan is running.");
            scanner.Hold.SetResult(true);
            PumpUntil(() => !workspace.IsScanning);
            Require(b.History.Entries.Count == 2 && a.History.Entries.Count == 1, "A run finishes in the Location it started in.");

            var temporary = LocationData.Temporary();
            Require(workspace.SetLocation(temporary), "The tab moves to a Temporary scan.");
            scanner.Hold = null;
            workspace.Scan();
            PumpUntil(() => !workspace.IsScanning && temporary.History.Entries.Count == 1);
            Require(!temporary.Devices.HasBaseline && workspace.NewDevices.Count == 0,
                "A Temporary scan sets no baseline and flags nothing.");
            temporary.Devices.SetName(workspace.Session.Devices.Single(), "Typed during the visit");
            int before = a.History.Entries.Count;
            Require(workspace.SaveResultsTo(a), "Temporary results can be saved to a Location.");
            Require(a.History.Entries.Count == before + 1 && a.Devices.GetName(device) == "Typed during the visit" &&
                !a.Devices.IsTrusted(device) && workspace.Location == a,
                "Saving results adds the run and its names to the Location, approves nothing, and moves the tab there.");
            Require(!workspace.SaveResultsTo(b), "Only a Temporary scan's results can be saved to a Location.");
            Suggestion(Path.Combine(root, "service"));
            AdapterPreference(Path.Combine(root, "adapter"));
        }
        finally
        {
            LocationService.Root = originalRoot;
            DemoData.Enabled = demo;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine("PASS: the scan tab names, records and baselines through its own Location.");
    }

    // The banner offers a Location this gateway was seen in, and never switches by itself.
    private static void Suggestion(string root)
    {
        LocationService.Root = root;
        LocationService.Load("Unassigned");
        bool createdAcme = LocationService.Create("Acme Dental", out var acme);
        bool createdShop = LocationService.Create("Shop", out var shop);
        Require(LocationService.Editable && createdAcme && createdShop, "Locations can be created in a scratch folder.");
        LocationService.AddGatewayMac(acme!, "AA:BB:CC:00:00:01");
        LocationService.AddGatewayMac(shop!, "AA:BB:CC:00:00:02");
        LocationService.AddGatewayMac(shop!, "AA:BB:CC:00:00:03");
        LocationService.AddGatewayMac(acme!, "AA:BB:CC:00:00:03");

        using var workspace = new ScanWorkspace("192.0.2.1", false, new SiteScanner());
        workspace.SetLocation(LocationData.Temporary());
        var banner = (System.Windows.Controls.Border)workspace.FindName("LocationSuggestion");
        var switchButton = (System.Windows.Controls.Button)workspace.FindName("LocationSuggestionSwitch");
        workspace.ShowLocationSuggestion("aa-bb-cc-00-00-01");
        Require(banner.Visibility == Visibility.Visible && switchButton.Visibility == Visibility.Visible && workspace.Location.IsTemporary,
            "A known gateway shows the banner and does not switch by itself.");
        switchButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Require(workspace.Location.Info?.Id == acme!.Id && banner.Visibility == Visibility.Collapsed, "Switch moves to the suggested Location.");

        workspace.ShowLocationSuggestion("AA:BB:CC:00:00:01");
        Require(banner.Visibility == Visibility.Collapsed, "Nothing is offered when already in the matching Location.");
        workspace.ShowLocationSuggestion("AA:BB:CC:00:00:03");
        Require(banner.Visibility == Visibility.Collapsed, "Nothing is offered when the current Location also matches.");
        workspace.SetLocation(LocationData.Temporary());
        workspace.ShowLocationSuggestion("AA:BB:CC:00:00:03");
        Require(banner.Visibility == Visibility.Visible && switchButton.Visibility == Visibility.Collapsed,
            "A gateway seen in two Locations lists both and offers no Switch.");
        ((System.Windows.Controls.Button)workspace.FindName("LocationSuggestionDismiss")).RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        workspace.ShowLocationSuggestion("AA:BB:CC:00:00:03");
        Require(banner.Visibility == Visibility.Collapsed, "A dismissed gateway is not offered again.");
        workspace.ShowLocationSuggestion("AA:BB:CC:00:00:99");
        Require(banner.Visibility == Visibility.Collapsed, "An unseen gateway offers nothing.");
    }

    private sealed class RoutePlatform : INetworkPlatform
    {
        internal string Adapter = "Wi-Fi";
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string>();
        public string ResolveMac(System.Net.IPAddress address) => "";
        public void FlushDnsCache() { }
        public (string Interface, string NextHop)? BestRoute(System.Net.IPAddress address) => (Adapter, "");
    }

    // The tab's adapter choice binds its scanner, travels with saved scans, refuses off-link
    // targets, and asks before falling back when the adapter is not connected.
    private static void AdapterPreference(string root)
    {
        var original = NetworkPlatform.Current;
        var platform = new RoutePlatform { Adapter = "Wi-Fi" };
        try
        {
            NetworkPlatform.Current = platform;
            var catalog = new LocationCatalog(root);
            catalog.Load("Unassigned");
            catalog.Create("Site", out var info);
            var site = catalog.Open(info!);
            var scanner = new SiteScanner();
            using var workspace = new ScanWorkspace("192.168.1.0/24", false, scanner);
            var ethernet = new ScanBinding("Ethernet", System.Net.IPAddress.Parse("192.168.1.5"), 24);
            bool connected = true;
            workspace.FindAdapter = name => connected && name == "Ethernet" ? ethernet : null;
            workspace.AvailableAdapters = () => connected ? [ethernet] : [];
            workspace.SetLocation(site);

            // Scan settings offers Automatic and each connected adapter.
            var settings = (System.Windows.Controls.Button)workspace.FindName("ScanOptionsButton");
            settings.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var adapterMenu = settings.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>()
                .Single(item => Equals(item.Header, Application.Current.FindResource("Str_Options_Adapter")));
            var choices = adapterMenu.Items.OfType<System.Windows.Controls.MenuItem>().ToList();
            Require(choices.Count == 2 && choices[0].IsChecked && Equals(choices[1].Header, "Ethernet"),
                "Network adapter lists Automatic, checked, and the connected adapter.");
            choices[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            settings.ContextMenu.IsOpen = false;
            Require(workspace.AdapterChoice == "Ethernet" && scanner.Binding == ethernet, "Choosing an adapter binds the tab's scanner.");

            Require(workspace.SaveScanAs("Office LAN"), "A scan can be saved in its Location.");
            Require(site.SavedScans.Items.Single().Adapter == "Ethernet", "Saving records the chosen adapter.");
            workspace.Scan();
            PumpUntil(() => !workspace.IsScanning && site.History.Entries.Count == 1);
            Require(site.History.Entries.Single().Adapter == "Ethernet", "A bound run records its adapter.");

            workspace.Targets = "10.0.0.0/24";
            workspace.Scan();
            Require(!workspace.IsScanning && site.History.Entries.Count == 1 &&
                workspace.Status == string.Format((string)Application.Current.FindResource("Str_St_AdapterOffLink"), "10.0.0.1", "Ethernet", "192.168.1.0/24"),
                "A target outside the adapter's network is refused with a clear status.");

            workspace.AdapterChoice = string.Empty;
            workspace.LoadSavedScan(site.SavedScans.Items.Single());
            Require(workspace.AdapterChoice == "Ethernet" && workspace.Targets == "192.168.1.0/24", "Loading a saved scan restores its adapter.");

            connected = false;
            string? asked = null;
            workspace.ConfirmAdapter = message => { asked = message; return false; };
            workspace.Scan();
            Require(asked != null && asked.Contains("Ethernet") && !workspace.IsScanning && site.History.Entries.Count == 1,
                "A disconnected adapter asks first, and declining scans nothing.");
            workspace.ConfirmAdapter = _ => true;
            workspace.Scan();
            PumpUntil(() => !workspace.IsScanning && site.History.Entries.Count == 2);
            Require(scanner.Binding == null && site.History.Entries.Last().Adapter == "Wi-Fi",
                "Falling back runs unbound and records the adapter Windows used.");

            string output = Path.Combine(root, "report.html");
            workspace.ExportDialog = dialog => { dialog.FileName = output; return true; };
            workspace.Export("html");
            Require(File.ReadAllText(output).Contains("<b>Wi-Fi</b>"), "The report shows the adapter the scan used.");
        }
        finally { NetworkPlatform.Current = original; }
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (complete() || elapsed.ElapsedMilliseconds > 3000) frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
