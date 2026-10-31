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
            Require(workspace.Session.Devices.Count == 1, "Saved results stay on screen in the Location they moved to.");
            ResultsFollowLocation(a, b, scanner);
            SaveResultsIsDurable(Path.Combine(root, "move"));
            Suggestion(Path.Combine(root, "service"));
            AdapterPreference(Path.Combine(root, "adapter"));
            LocationControls(Path.Combine(root, "controls"));
            DeletedElsewhere(Path.Combine(root, "deleted"));
        }
        finally
        {
            LocationService.Root = originalRoot;
            DemoData.Enabled = demo;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine("PASS: the scan tab names, records and baselines through its own Location.");
    }

    // Results on screen belong to the Location they were scanned in, so switching clears them
    // rather than letting a rename or approval land in the new Location.
    private static void ResultsFollowLocation(LocationData a, LocationData b, SiteScanner scanner)
    {
        using var workspace = new ScanWorkspace("192.168.1.10", false, scanner);
        workspace.SetLocation(a);
        workspace.Scan();
        PumpUntil(() => !workspace.IsScanning && workspace.Session.Devices.Count == 1);

        Require(workspace.SetLocation(a) && workspace.Session.Devices.Count == 1 && workspace.Location == a,
            "Picking the current Location again changes nothing, results included.");
        Require(workspace.SetLocation(b) && workspace.Session.Devices.Count == 0 && workspace.Location == b,
            "Switching Location clears the results scanned in the old one.");

        var temporary = LocationData.Temporary();
        workspace.SetLocation(temporary);
        workspace.Scan();
        PumpUntil(() => !workspace.IsScanning && workspace.Session.Devices.Count == 1);
        string? asked = null;
        workspace.ConfirmDiscard = message => { asked = message; return false; };
        Require(!workspace.SetLocation(a) && workspace.Location == temporary && workspace.Session.Devices.Count == 1 &&
                asked == (string)Application.Current.FindResource("Str_Loc_DiscardTemporary"),
            "Leaving unsaved Temporary results asks first, and No keeps them.");
        Require(workspace.SetLocation(LocationData.Temporary()) && workspace.Session.Devices.Count == 1,
            "Picking Temporary scan while already in one changes nothing.");
        workspace.ConfirmDiscard = _ => true;
        Require(workspace.SetLocation(a) && workspace.Location == a && workspace.Session.Devices.Count == 0,
            "Discarding moves to the Location with an empty tab.");
    }

    private static HistoryStore Reread(string path)
    {
        var store = new HistoryStore(path);
        store.Load();
        return store;
    }

    // Saving unsaved results moves them only once every write has landed. Until then the tab stays
    // where it was, with its results, and the run is on disk at least once. Saving again after a
    // failure finishes the move without doubling anything.
    private static void SaveResultsIsDurable(string root)
    {
        // Files from an earlier version make the first start create Unassigned.
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "devices.json"), "{}");
        LocationService.Root = root;
        LocationService.Load("Unassigned");
        Require(LocationService.Create("Keeps one", out var keepsOneInfo), "A Location is created.");
        Require(LocationService.Create("Site", out var siteInfo), "A Location is created.");
        var unassigned = LocationService.Get(LocationService.Find(LocationCatalog.UnassignedId)!);
        var keepsOne = LocationService.Get(keepsOneInfo!);
        var site = LocationService.Get(siteInfo!);
        string FileOf(LocationData data, string name) => Path.Combine(root, "locations", data.Info!.Id, name);
        var asked = new List<string>();
        Func<bool> answer = () => false;
        PersistenceFeedback.Answer = message => { asked.Add(message); return answer(); };
        using var workspace = new ScanWorkspace("192.168.1.10", false, new SiteScanner());
        workspace.ConfirmDiscard = _ => true;
        ScanHistoryEntry ScanIn(LocationData location)
        {
            workspace.SetLocation(location);
            int before = location.History.Entries.Count;
            workspace.Scan();
            PumpUntil(() => !workspace.IsScanning && location.History.Entries.Count == before + 1);
            Require(workspace.Session.Devices.Count == 1, "The scan is on screen.");
            return location.History.Entries.Last();
        }
        bool Stayed(LocationData location) => workspace.Location == location && workspace.Session.Devices.Count == 1;
        int Copies(LocationData location, ScanHistoryEntry run) => Reread(FileOf(location, "history.json")).Entries.Count(entry => entry.IsSameRun(run));

        try
        {
            // An older run on screen, and a destination that keeps only its newest run.
            Require(keepsOne.History.ApplyRetention(new HistoryRetention(HistoryRetentionMode.Count, 1), _ => true), "The destination keeps one run.");
            Require(keepsOne.History.Adopt([new ScanHistoryEntry { Target = "newer", ScannedAt = DateTimeOffset.Now.AddDays(1), Completion = ScanCompletion.Complete }]) == AdoptResult.Saved,
                "The destination holds a newer run.");
            var older = ScanIn(unassigned);
            Require(!workspace.SaveResultsTo(keepsOne) && Stayed(unassigned), "A run the destination would drop is refused and the tab keeps it.");
            Require(workspace.Status == string.Format((string)Application.Current.FindResource("Str_Loc_SaveRetention"), "Keeps one"), "The status says why nothing moved.");
            Require(Copies(unassigned, older) == 1 && Reread(FileOf(keepsOne, "history.json")).Entries.Single().Target == "newer"
                    && keepsOne.SavedScans.Items.Count == 0 && asked.Count == 0, "A refused run writes nothing and stays in the source.");

            // The destination's history cannot be written, and the retry is declined.
            string siteHistory = FileOf(site, "history.json");
            Require(site.History.Adopt([new ScanHistoryEntry { Target = "existing", ScannedAt = DateTimeOffset.Now.AddHours(-1), Completion = ScanCompletion.Complete }]) == AdoptResult.Saved,
                "The destination holds a run.");
            using (new FileStream(siteHistory, FileMode.Open, FileAccess.Read, FileShare.None))
                Require(!workspace.SaveResultsTo(site) && Stayed(unassigned) && asked.Count == 1, "A declined retry reports failure and stays put.");
            Require(workspace.Status == string.Format((string)Application.Current.FindResource("Str_Loc_SaveIncomplete"), "Site"), "The status says the save did not finish.");
            Require(Copies(unassigned, older) == 1 && Copies(site, older) == 0, "Nothing moved.");

            // The same failure, with the file freed during Retry.
            asked.Clear();
            var held = new FileStream(siteHistory, FileMode.Open, FileAccess.Read, FileShare.None);
            answer = () => { held.Dispose(); return true; };
            Require(workspace.SaveResultsTo(site) && Stayed(site) && asked.Count == 1, "Retry finishes the move.");
            Require(Copies(unassigned, older) == 0 && Copies(site, older) == 1, "The run moved, once.");
            answer = () => false;

            // The saved scan cannot be written after the history landed.
            var temporary = LocationData.Temporary();
            var typed = ScanIn(temporary);
            using (new FileStream(FileOf(site, "profiles.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                Require(!workspace.SaveResultsTo(site, "Office") && Stayed(temporary), "A failed saved scan leaves the tab on its results.");
            Require(Copies(site, typed) == 1, "The history had landed.");
            Require(workspace.SaveResultsTo(site, "Office") && Stayed(site) && Copies(site, typed) == 1, "Saving again finishes the move without a second copy.");
            var scans = new SavedScanStore(FileOf(site, "profiles.json"));
            scans.Load();
            Require(scans.Items.Any(item => item.Name == "Office"), "The saved scan reached the file.");

            // A typed name cannot be written.
            temporary = LocationData.Temporary();
            typed = ScanIn(temporary);
            Require(temporary.Devices.SetName(workspace.Session.Devices.Single(), "Typed name"), "A name is typed.");
            Require(site.Devices.SetName(new NetworkDevice { IpAddress = "192.168.1.11" }, "Already named"), "The destination has a devices file to block.");
            using (new FileStream(FileOf(site, "devices.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                Require(!workspace.SaveResultsTo(site) && Stayed(temporary), "A failed name leaves the tab on its results.");
            Require(workspace.SaveResultsTo(site) && Stayed(site) && Copies(site, typed) == 1, "Saving again finishes the names.");
            var names = new DevicePreferenceStore(FileOf(site, "devices.json"));
            names.Load();
            Require(names.GetName(new NetworkDevice { IpAddress = "192.168.1.10" }) == "Typed name", "The name reached the file.");

            // The source cannot let go of the run after the destination has it.
            var moved = ScanIn(unassigned);
            using (new FileStream(FileOf(unassigned, "history.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                Require(!workspace.SaveResultsTo(site) && Stayed(unassigned), "A source that cannot be changed leaves the tab where it was.");
            Require(Copies(unassigned, moved) == 1 && Copies(site, moved) == 1, "The run is in both files, never in neither.");
            Require(workspace.SaveResultsTo(site) && Stayed(site), "Saving again finishes the move.");
            Require(Copies(unassigned, moved) == 0 && Copies(site, moved) == 1, "The source lets go only after the destination has it.");

            // Another window removed the run from Unassigned in the meantime.
            var gone = ScanIn(unassigned);
            Require(Reread(FileOf(unassigned, "history.json")).Remove(gone), "Another window deletes the run.");
            asked.Clear();
            Require(workspace.SaveResultsTo(site) && Stayed(site) && asked.Count == 0 && Copies(site, gone) == 1, "A run already gone from the source is not an error.");

            // An empty scan bar has no saved scan to write, which does not stop the results moving.
            temporary = LocationData.Temporary();
            typed = ScanIn(temporary);
            int savedScans = site.SavedScans.Items.Count;
            workspace.Targets = string.Empty;
            Require(workspace.SaveResultsTo(site) && Stayed(site) && Copies(site, typed) == 1 && site.SavedScans.Items.Count == savedScans,
                "Results move without a saved scan when the scan bar is empty.");
        }
        finally { PersistenceFeedback.Answer = null; }
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

    // Serves the gateway's MAC from the ARP cache and fails any attempt to ask the network.
    private sealed class CachePlatform(string gateway, string mac) : INetworkPlatform
    {
        internal int Asked;
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string> { [gateway] = mac };
        public string ResolveMac(System.Net.IPAddress address) { Interlocked.Increment(ref Asked); return mac; }
        public void FlushDnsCache() { }
        public (string Interface, string NextHop)? BestRoute(System.Net.IPAddress address) => ("Ethernet", "");
    }

    // The suggestion banner, the Location label, the detail menus and import, beyond scanning.
    // Another KillerScan deletes the tab's Location. The tab must stop writing there, or the
    // deleted folder comes back holding work nothing lists.
    private static void DeletedElsewhere(string root)
    {
        LocationService.Root = root;
        LocationService.Load("Unassigned");
        Require(LocationService.Create("Gone Site", out var gone), "A Location is created.");
        var scanner = new SiteScanner();
        using var workspace = new ScanWorkspace("192.168.1.10", false, scanner);
        Require(workspace.OpenLocation(gone!), "The tab opens it.");
        var otherProcess = new LocationCatalog(root);
        otherProcess.Load("Unassigned");
        Require(otherProcess.Delete(gone!.Id, out _), "Another KillerScan deletes it.");
        string folder = Path.Combine(root, "locations", gone.Id);
        Require(!Directory.Exists(folder), "The deleted Location's folder is gone.");

        // A scan that finishes after the deletion keeps its results as a Temporary scan.
        workspace.Scan();
        PumpUntil(() => !workspace.IsScanning);
        Require(workspace.Location.IsTemporary && workspace.Session.Devices.Count == 1, "The finished scan stays on screen as a Temporary scan.");
        Require(!Directory.Exists(folder), "Nothing recreates the deleted Location's folder.");
        Require(LocationService.Find(gone.Id) == null, "The Locations list no longer shows it.");
        Require(workspace.Status == string.Format((string)Application.Current.FindResource("Str_Loc_DeletedElsewhere"), "Gone Site"),
            "The status says why the tab moved.");
    }

    private static void LocationControls(string root)
    {
        LocationService.Root = root;
        LocationService.Load("Unassigned");
        LocationService.Create("Acme Dental", out var acme);
        string? gateway = LocalNetwork.Detect()?.Gateway;
        var original = NetworkPlatform.Current;
        using var workspace = new ScanWorkspace("192.0.2.1", false, new SiteScanner());
        try
        {
            workspace.SetLocation(LocationData.Temporary());
            var banner = (System.Windows.Controls.Border)workspace.FindName("LocationSuggestion");
            if (!string.IsNullOrEmpty(gateway))
            {
                // The banner reads the gateway's MAC from the ARP cache only; it never asks the network.
                LocationService.AddGatewayMac(acme!, "AA:BB:CC:00:10:01");
                var platform = new CachePlatform(gateway!, "AA:BB:CC:00:10:01");
                NetworkPlatform.Current = platform;
                typeof(ScanWorkspace).GetMethod("CheckLocationSuggestion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(workspace, null);
                PumpUntil(() => banner.Visibility == Visibility.Visible);
                Require(banner.Visibility == Visibility.Visible && platform.Asked == 0, "The suggestion comes from the ARP cache without sending anything.");

                // Picking the suggested Location another way hides the banner; leaving it brings it back.
                Require(workspace.OpenLocation(LocationService.Find(acme!.Id)!) && banner.Visibility == Visibility.Collapsed,
                    "The banner goes away once the tab is in the suggested Location.");
                workspace.SetLocation(LocationData.Temporary());
                Require(banner.Visibility == Visibility.Visible, "Leaving the suggested Location offers it again.");
            }
            else Console.WriteLine("NOTE: no default gateway, so the banner checks were skipped.");

            var text = (System.Windows.Controls.TextBlock)workspace.FindName("LocationSuggestionText");
            var switchButton = (System.Windows.Controls.Button)workspace.FindName("LocationSuggestionSwitch");
            var dismiss = (System.Windows.Controls.Button)workspace.FindName("LocationSuggestionDismiss");
            Require(System.Windows.Automation.AutomationProperties.GetLiveSetting(text) == System.Windows.Automation.AutomationLiveSetting.Polite &&
                    System.Windows.Input.KeyboardNavigation.GetTabIndex(switchButton) < System.Windows.Input.KeyboardNavigation.GetTabIndex(dismiss),
                "The banner text is a live region and Tab reaches Switch before Dismiss.");

            // A language change relabels the Location button.
            var label = (System.Windows.Controls.TextBlock)workspace.FindName("LocationLabel");
            object previous = Application.Current.Resources["Str_Loc_Temporary"];
            try
            {
                Application.Current.Resources["Str_Loc_Temporary"] = "Temporary (other language)";
                workspace.RefreshLocale();
                Require(label.Text == "Temporary (other language)", "The Location button follows a language change.");
            }
            finally { Application.Current.Resources["Str_Loc_Temporary"] = previous; workspace.RefreshLocale(); }

            // A detail in the shortcut column is help text to a screen reader, not a shortcut.
            var detail = new DetailMenuItem { Header = "Office LAN", InputGestureText = "10.0.0.0/24" };
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(detail)!;
            Require(peer.GetAcceleratorKey().Length == 0 && peer.GetHelpText() == "10.0.0.0/24", "A menu detail is read as help, not as a shortcut.");

            // Import refuses an oversized file before reading it, and runs off the UI thread.
            string? failed = null;
            workspace.ImportFailed = message => failed = message;
            string big = Path.Combine(root, "big.ksloc");
            File.WriteAllBytes(big, new byte[4096]);
            long cap = ScanWorkspace.MaxImportBytes;
            try
            {
                ScanWorkspace.MaxImportBytes = 1024;
                var import = workspace.ImportLocationFrom(big);
                PumpUntil(() => import.IsCompleted);
                Require(failed == string.Format((string)Application.Current.FindResource("Str_Loc_ImportTooLarge"), 1) && LocationService.Items.Count == 1,
                    "An oversized Location file is refused and adds nothing.");
            }
            finally { ScanWorkspace.MaxImportBytes = cap; }
            string good = Path.Combine(root, "good.ksloc");
            File.WriteAllText(good, "{\"Format\":\"KillerScan.Location\",\"Version\":1,\"Name\":\"Shop\",\"Files\":{}}");
            failed = null;
            var ok = workspace.ImportLocationFrom(good);
            PumpUntil(() => ok.IsCompleted);
            Require(failed == null && workspace.LocationName == "Shop" && LocationService.Items.Count == 2, "A Location imports and the tab moves to it: " + failed);
        }
        finally { NetworkPlatform.Current = original; }
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
            // Through LocationService, as the app does: the last-used adapter is recorded there,
            // so a private catalog would never see it.
            LocationService.Root = root;
            LocationService.Load("Unassigned");
            Require(LocationService.Create("Site", out var info), "A Location is created.");
            var site = LocationService.Get(info!);
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

            workspace.Scan();
            PumpUntil(() => !workspace.IsScanning && site.History.Entries.Count == 1);
            Require(site.History.Entries.Single().Adapter == "Ethernet", "A bound run records its adapter.");
            Require(LocationService.Find(site.Info!.Id)?.LastAdapter == "Ethernet", "The Location remembers the adapter of its last run.");

            // The fake platform routes 10.0.0.1 through Wi-Fi, not the bound Ethernet.
            workspace.Targets = "10.0.0.0/24";
            workspace.Scan();
            Require(!workspace.IsScanning && site.History.Entries.Count == 1 &&
                workspace.Status == string.Format((string)Application.Current.FindResource("Str_St_AdapterOffLink"), "10.0.0.1", "Ethernet", "192.168.1.0/24"),
                "A target outside the adapter's network is refused with a clear status.");


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
