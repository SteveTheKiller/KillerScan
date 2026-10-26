using System;
using System.IO;
using System.Linq;
using KillerScan.Engine;
using KillerScan.Services;

// Locations keep each site's data apart, and the first start moves existing data into
// Unassigned without changing or losing the original files.
internal static class LocationTests
{
    internal static void Run()
    {
        bool demo = DemoData.Enabled;
        DemoData.Enabled = false;
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-locations-" + Guid.NewGuid().ToString("N"));
        try
        {
            FreshInstall(Path.Combine(root, "fresh"));
            Migration(Path.Combine(root, "migrate"));
            InterruptedMigration(Path.Combine(root, "interrupted"));
            CorruptCatalog(Path.Combine(root, "corrupt"));
            UnusableCatalog(Path.Combine(root, "unusable"));
            UnreadableAfterMigration(Path.Combine(root, "stale"));
            ConcurrentMigration(Path.Combine(root, "concurrent"));
            LeftoverUnassigned(Path.Combine(root, "leftover"));
            FailedCatalogChange(Path.Combine(root, "busy"));
            FailedImportMove(Path.Combine(root, "move"));
            DeletedDuringScan(Path.Combine(root, "deleted"));
            BaselineByTime(Path.Combine(root, "time"));
            ReloadKeepsUnsaved(Path.Combine(root, "reload"));
            Isolation(Path.Combine(root, "isolation"));
            KnownAndApproved(Path.Combine(root, "known"));
            CommandLine(Path.Combine(root, "cli"));
            Transfer(Path.Combine(root, "transfer"));
            Suggestions(Path.Combine(root, "suggest"));
            Deletion(Path.Combine(root, "delete"));
        }
        finally
        {
            DemoData.Enabled = demo;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine("PASS: Locations migrate existing data once and keep each site's data separate.");
    }

    private static NetworkDevice Device(string ip, string mac = "") => new() { IpAddress = ip, MacAddress = mac };

    private static void FreshInstall(string root)
    {
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        Require(catalog.LoadError == null && catalog.Items.Count == 0, "A fresh install has no Locations and no error.");
        Require(File.Exists(Path.Combine(root, "locations.json")), "A fresh install publishes an empty catalog.");
    }

    private static void SeedLegacy(string root)
    {
        Directory.CreateDirectory(root);
        var devices = new DevicePreferenceStore(Path.Combine(root, "devices.json"));
        devices.SetName(Device("192.168.1.10"), "Front desk PC");
        devices.SetTrusted(Device("192.168.1.10"), true);
        new DeviceOverrideStore(Path.Combine(root, "overrides.json")).Set("00:11:22:33:44:55", "Printer");
        new DeviceLoginStore(Path.Combine(root, "logins.json")).Set("", "192.168.1.10", "admin");
        new SavedScanStore(Path.Combine(root, "profiles.json")).Save(new ScanProfile { Name = "Office LAN", Target = "192.168.1.0/24", DeepScanAfter = true });
        var history = new HistoryStore(Path.Combine(root, "history.json"));
        history.Load();
        history.Record("192.168.1.0/24", [Device("192.168.1.10")]);
    }

    private static readonly string[] StoreFiles = ["devices.json", "overrides.json", "logins.json", "profiles.json", "history.json"];

    private static void Migration(string root)
    {
        SeedLegacy(root);
        var originals = StoreFiles.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, name)));
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        Require(catalog.LoadError == null && catalog.Items.Count == 1 && catalog.Items[0].Name == "Unassigned",
            "Existing data moves into one Location named Unassigned.");
        foreach (var pair in originals)
            Require(File.ReadAllBytes(Path.Combine(root, pair.Key)).SequenceEqual(pair.Value), "The original " + pair.Key + " is left untouched.");

        var data = catalog.Open(catalog.Items[0]);
        Require(data.Devices.GetName(Device("192.168.1.10")) == "Front desk PC" && data.Devices.IsTrusted(Device("192.168.1.10")),
            "Unassigned keeps device names and trust.");
        Require(data.Overrides.Get("00:11:22:33:44:55") == "Printer" && data.Logins.Get("", "192.168.1.10") == "admin",
            "Unassigned keeps type overrides and SSH usernames.");
        Require(data.SavedScans.Items.Single().DeepScanAfter && data.History.Entries.Count == 1,
            "Unassigned keeps saved scans with their settings, and history.");

        var again = new LocationCatalog(root);
        again.Load("Unassigned");
        Require(again.Items.Count == 1 && again.Items[0].Id == catalog.Items[0].Id, "Migration runs once.");
    }

    private static void InterruptedMigration(string root)
    {
        SeedLegacy(root);
        // A previous attempt stopped after staging part of the copy and before the catalog.
        string staging = Path.Combine(root, "locations", ".migrating");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "devices.json"), "{partial");
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        Require(catalog.LoadError == null && catalog.Items.Count == 1, "An interrupted migration completes on the next start.");
        Require(catalog.Open(catalog.Items[0]).Devices.GetName(Device("192.168.1.10")) == "Front desk PC",
            "The retried migration copies complete files, not the partial ones.");
        Require(!Directory.Exists(staging), "The staging folder is gone after a successful migration.");
    }

    private static void CorruptCatalog(string root)
    {
        SeedLegacy(root);
        File.WriteAllText(Path.Combine(root, "locations.json"), "{broken");
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        Require(catalog.LoadError != null && catalog.Items.Count == 0, "A corrupt catalog is reported, not replaced by a new migration.");
        Require(File.ReadAllText(Path.Combine(root, "locations.json")) == "{broken", "The corrupt catalog keeps its bytes.");
        Require(!catalog.Create("New site", out _), "Nothing is added to a catalog that could not be read.");
    }

    // A catalog that parses but cannot be used, from a newer version or holding a literal null,
    // is reported like a corrupt one. It used to escape as an exception and stop the app.
    private static void UnusableCatalog(string root)
    {
        foreach (string content in new[] { "{\"Version\":2,\"Locations\":[]}", "null" })
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "locations.json"), content);
            var catalog = new LocationCatalog(root);
            catalog.Load("Unassigned");
            Require(catalog.LoadError != null && catalog.Items.Count == 0 && !catalog.Create("New site", out _),
                "An unusable catalog is reported and changes nothing: " + content);
            Require(File.ReadAllText(Path.Combine(root, "locations.json")) == content, "The unusable catalog keeps its bytes: " + content);
        }
    }

    // After migration the app-wide files are a stale backup. When the catalog cannot be read,
    // the app runs Temporary scans rather than writing to them.
    private static void UnreadableAfterMigration(string root)
    {
        SeedLegacy(root);
        var originals = StoreFiles.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, name)));
        File.WriteAllText(Path.Combine(root, "locations.json"), "{broken");
        string previous = LocationService.Root;
        try
        {
            LocationService.Root = root;
            LocationService.Load("Unassigned");
            var data = LocationService.Get(new LocationInfo { Id = LocationCatalog.UnassignedId, Name = "Unassigned" });
            Require(LocationService.LoadError != null && !LocationService.Editable && LocationService.Items.Count == 0 && data.IsTemporary,
                "An unreadable catalog after migration gives Temporary scans and reports why.");
            data.Devices.SetName(Device("192.168.1.10"), "Typed while broken");
            data.History.Record("192.168.1.0/24", [Device("192.168.1.10")]);
            foreach (var pair in originals)
                Require(File.ReadAllBytes(Path.Combine(root, pair.Key)).SequenceEqual(pair.Value), "The stale " + pair.Key + " is never written.");
        }
        finally { LocationService.Root = previous; }
    }

    // Two windows starting together migrate once, and the data they both see is intact.
    private static void ConcurrentMigration(string root)
    {
        SeedLegacy(root);
        var catalogs = new[] { new LocationCatalog(root), new LocationCatalog(root) };
        var threads = catalogs.Select(catalog => new System.Threading.Thread(() => catalog.Load("Unassigned"))).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());
        foreach (var catalog in catalogs)
            Require(catalog.LoadError == null && catalog.Items.Count == 1 && catalog.Items[0].Id == LocationCatalog.UnassignedId,
                "Both windows see one Unassigned Location: " + catalog.LoadError);
        Require(catalogs[0].Open(catalogs[0].Items[0]).Devices.GetName(Device("192.168.1.10")) == "Front desk PC",
            "The migrated data survives two windows starting together.");
        Require(!Directory.Exists(Path.Combine(root, "locations", ".deleted")), "A single migration sets nothing aside.");
    }

    // A folder left by a migration that stopped before the catalog is set aside, never deleted.
    private static void LeftoverUnassigned(string root)
    {
        SeedLegacy(root);
        string leftover = Path.Combine(root, "locations", LocationCatalog.UnassignedId);
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "marker.txt"), "written after the earlier move");
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        Require(catalog.LoadError == null && catalog.Open(catalog.Items.Single()).Devices.GetName(Device("192.168.1.10")) == "Front desk PC",
            "Migration still completes over a leftover folder.");
        var kept = Directory.GetDirectories(Path.Combine(root, "locations", ".deleted"), LocationCatalog.UnassignedId + "-*").Single();
        Require(File.ReadAllText(Path.Combine(kept, "marker.txt")) == "written after the earlier move", "The leftover folder is kept under .deleted.");
    }

    // A catalog change that cannot be saved is not listed now and not replayed later.
    private static void FailedCatalogChange(string root)
    {
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        // Another window holding the catalog's save lock for longer than the 2 s wait.
        string path = Path.GetFullPath(Path.Combine(root, "locations.json"));
        string lockName = KillerScan.Services.ProfileLock.Name("Preferences", path);
        // Session-scoped, so another account on a shared computer cannot create and hold it.
        Require(lockName.StartsWith("Local\\KillerScan.Preferences.", StringComparison.Ordinal), "Profile locks are session-scoped: " + lockName);
        var held = new System.Threading.ManualResetEventSlim();
        var release = new System.Threading.ManualResetEventSlim();
        var holder = new System.Threading.Thread(() =>
        {
            using var mutex = new System.Threading.Mutex(false, lockName);
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait();
        bool created = catalog.Create("Ghost", out _);
        release.Set();
        holder.Join();
        Require(!created && catalog.Items.Count == 0, "A Location whose save failed is not listed.");
        Require(catalog.Create("Real", out _) && catalog.Items.Select(item => item.Name).SequenceEqual(["Real"]),
            "The failed change is not replayed by the next save.");
    }

    // An import whose files cannot be moved into place leaves no Location behind.
    private static void FailedImportMove(string root)
    {
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        byte[] file = System.Text.Encoding.UTF8.GetBytes("{\"Format\":\"KillerScan.Location\",\"Version\":1,\"Name\":\"Moved\",\"Customer\":\"Acme\",\"Notes\":\"n\",\"Files\":{}}");
        var move = LocationTransfer.MoveDirectory;
        try
        {
            LocationTransfer.MoveDirectory = (_, _) => throw new IOException("The folder is in use.");
            Require(!LocationTransfer.Import(catalog, file, out _, out string? error) && error == "The folder is in use." && catalog.Items.Count == 0,
                "A failed move takes the new entry off the list again.");
        }
        finally { LocationTransfer.MoveDirectory = move; }
        Require(LocationTransfer.Import(catalog, file, out var imported, out _) && imported!.Customer == "Acme" && imported.Notes == "n",
            "An import saves its customer and notes with the entry.");
    }

    // A terminal scan checks its Location is still there before recording into it.
    private static void DeletedDuringScan(string root)
    {
        string previous = LocationService.Root;
        try
        {
            LocationService.Root = root;
            LocationService.Load("Unassigned");
            Require(LocationService.Create("Site", out var site), "A Location is created.");
            Require(LocationService.StillExists(site!.Id), "A Location in the catalog still exists.");
            var otherWindow = new LocationCatalog(root);
            otherWindow.Load("Unassigned");
            Require(otherWindow.Delete(site.Id, out _), "Another window deletes it.");
            Require(!LocationService.StillExists(site.Id), "The deletion is seen without reloading this process.");
        }
        finally { LocationService.Root = previous; }
    }

    // History compares with the latest earlier run by time, even after an older run is adopted.
    private static void BaselineByTime(string root)
    {
        var history = new HistoryStore(Path.Combine(root, "history.json"));
        history.Load();
        var temporary = LocationData.Temporary().History;
        temporary.Record("10.3.0.0/24", "10.3.0.0/24", [Device("10.3.0.1")], ScanCompletion.Complete, new ScanOptions(), deep: false);
        System.Threading.Thread.Sleep(20);
        history.Record("10.3.0.0/24", "10.3.0.0/24", [Device("10.3.0.2")], ScanCompletion.Complete, new ScanOptions(), deep: false);
        var newer = history.Entries.Last();
        history.Adopt(temporary.Entries.Single());
        System.Threading.Thread.Sleep(20);
        var latest = history.Record("10.3.0.0/24", "10.3.0.0/24", [Device("10.3.0.2")], ScanCompletion.Complete, new ScanOptions(), deep: false);
        Require(latest.Previous?.ScannedAt == newer.ScannedAt, "The baseline is the newest earlier run, not the one adopted last.");
        Require(history.Compare(history.Entries.Last()).Previous?.ScannedAt == newer.ScannedAt, "Viewing the run later picks the same baseline.");
    }

    // Rereading history after another process wrote keeps a run this process could not save.
    private static void ReloadKeepsUnsaved(string root)
    {
        string path = Path.Combine(root, "history.json");
        var history = new HistoryStore(path);
        history.Load();
        history.Record("10.4.0.0/24", "10.4.0.0/24", [Device("10.4.0.1")], ScanCompletion.Complete, new ScanOptions(), deep: false);
        bool saved;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            saved = history.Record("10.4.0.0/24", "10.4.0.0/24", [Device("10.4.0.2")], ScanCompletion.Complete, new ScanOptions(), deep: false).Saved;
        Require(!saved && history.Entries.Count == 2, "A run that could not be saved is kept for a retry.");
        history.Reload();
        Require(history.Entries.Count == 2, "Reloading keeps the unsaved run.");
        Require(history.Retry(), "The kept run saves on retry.");
        var reread = new HistoryStore(path);
        reread.Load();
        Require(reread.Entries.Count == 2, "Both runs are in the file after the retry.");
    }

    private static void Isolation(string root)
    {
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        bool first = catalog.Create("Acme Dental", out var acme);
        bool second = catalog.Create("Acme Dental", out var other);
        Require(first && second, "Two Locations may share a name.");
        Require(acme!.Id != other!.Id, "Each Location gets its own ID.");
        var a = catalog.Open(acme);
        var b = catalog.Open(other);
        var noMac = Device("192.168.1.10");
        a.Devices.SetName(noMac, "Reception");
        a.Devices.SetTrusted(noMac, true);
        a.Logins.Set("", "192.168.1.10", "admin");
        a.SavedScans.Save(new ScanProfile { Name = "Office LAN", Target = "192.168.1.0/24" });
        b.SavedScans.Save(new ScanProfile { Name = "Office LAN", Target = "10.0.0.0/24" });
        a.History.Record("192.168.1.0/24", [noMac]);
        Require(b.Devices.GetName(noMac) == null && !b.Devices.IsTrusted(noMac) && b.Logins.Get("", "192.168.1.10") == null,
            "Names, trust and usernames never cross to another Location with the same IP.");
        Require(b.History.Entries.Count == 0 && b.SavedScans.Items.Single().Target == "10.0.0.0/24",
            "History and same-named saved scans stay in their own Location.");

        Require(catalog.Rename(acme.Id, "Acme Dental (Main St)"), "A Location can be renamed.");
        var reloaded = new LocationCatalog(root);
        reloaded.Load("Unassigned");
        var renamed = reloaded.Items.Single(item => item.Id == acme.Id);
        Require(renamed.Name == "Acme Dental (Main St)" && reloaded.Open(renamed).Devices.GetName(noMac) == "Reception",
            "Renaming keeps the ID and the data after a restart.");

        var temporary = LocationData.Temporary();
        temporary.Devices.SetName(noMac, "Scratch");
        Require(temporary.Devices.GetName(noMac) == "Scratch" && reloaded.Open(renamed).Devices.GetName(noMac) == "Reception" &&
            !Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Any(file => File.ReadAllText(file).Contains("Scratch")),
            "A Temporary scan's data stays in memory.");
    }

    private static void KnownAndApproved(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "devices.json");
        NetworkDevice a = Device("10.0.0.1", "00:00:00:00:00:01"), b = Device("10.0.0.2", "00:00:00:00:00:02"),
            c = Device("10.0.0.3", "00:00:00:00:00:03"), d = Device("10.0.0.4");
        var store = new DevicePreferenceStore(path);
        store.Load();
        var first = store.Observe([a, b]);
        Require(first.BaselineCreated && first.NewDevices.Count == 0, "The first scan sets the baseline and flags nothing.");
        Require(!store.IsTrusted(a) && !store.IsTrusted(b) && store.IsKnown(a), "The first scan makes devices Known, not Approved.");
        var second = store.Observe([a, b, c]);
        Require(!second.BaselineCreated && second.NewDevices.Single() == c, "A later scan flags only devices new since the baseline.");
        Require(store.Observe([a, b, c]).NewDevices.Count == 0, "A device is flagged once, then Known.");

        store.SetTrusted(a, true);
        store.SetTrusted(a, false);
        var afterRemoval = new DevicePreferenceStore(path);
        afterRemoval.Load();
        var third = afterRemoval.Observe([a, b, c, d]);
        Require(afterRemoval.HasBaseline && !third.BaselineCreated && third.NewDevices.Single() == d,
            "Removing the last approved device keeps the baseline after a restart.");
        Require(!new[] { a, b, c, d }.Any(afterRemoval.IsTrusted), "Nothing is ever approved automatically.");

        string legacyPath = Path.Combine(root, "legacy.json");
        File.WriteAllText(legacyPath, "{\"Names\":{},\"Trusted\":[\"mac:00:00:00:00:00:01\"]}");
        var legacy = new DevicePreferenceStore(legacyPath);
        legacy.Load();
        Require(legacy.HasBaseline && legacy.IsTrusted(a) && legacy.IsKnown(a), "Older trust lists become the baseline.");
        Require(legacy.Observe([a, b]).NewDevices.Single() == b, "An older install keeps flagging devices outside its trusted list once.");
    }

    private static void CommandLine(string root)
    {
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        catalog.Create("Acme Dental", out var acme);
        catalog.Create("Branch", out var first);
        catalog.Create("branch", out var second);
        var items = catalog.Items;
        Require(KillerScan.Features.Cli.CliRunner.ResolveLocation("acme dental", items, out _)?.Id == acme!.Id,
            "/location finds a Location by name in any letter case.");
        Require(KillerScan.Features.Cli.CliRunner.ResolveLocation(first!.Id, items, out _)?.Id == first.Id,
            "/location finds a Location by ID.");
        Require(KillerScan.Features.Cli.CliRunner.ResolveLocation("Nowhere", items, out string? missing) == null &&
            missing!.Contains("Nowhere"), "An unknown name is refused with a message.");
        Require(KillerScan.Features.Cli.CliRunner.ResolveLocation("Branch", items, out string? ambiguous) == null &&
            ambiguous!.Contains(first.Id) && ambiguous.Contains(second!.Id), "A shared name is refused and lists the IDs.");

        var data = catalog.Open(acme);
        var err = new StringWriter();
        var devices = new[] { Device("10.1.0.1", "00:00:00:00:01:01"), Device("10.1.0.2", "00:00:00:00:01:02") };
        KillerScan.Features.Cli.CliRunner.RecordToLocation(data, "10.1.0.0/24", "10.1.0.0/24", devices, quick: false, err, quiet: false, keyed: true);
        Require(data.History.Entries.Count == 1 && data.Devices.HasBaseline && !devices.Any(data.Devices.IsTrusted),
            "A command-line scan records history and the baseline, and approves nothing.");
        Require(err.ToString().Contains("Str_St_KnownBaseline|2"), "The in-app terminal is told the baseline was set.");
        err = new StringWriter();
        KillerScan.Features.Cli.CliRunner.RecordToLocation(data, "10.1.0.0/24", "10.1.0.0/24",
            [.. devices, Device("10.1.0.3", "00:00:00:00:01:03")], quick: false, err, quiet: false, keyed: false);
        Require(err.ToString().Contains("1 devices are new"), "A later command-line scan reports devices new since the baseline.");
        Require(data.History.Entries.Last().TargetKey == "10.1.0.0/24", "Command-line history keeps the full targets.");

        Require(KillerScan.Features.Cli.CliRunner.ParseSkip(null, out var none, out _) && none.DisabledFeatures.Length == 0,
            "Without /skip every check runs.");
        Require(KillerScan.Features.Cli.CliRunner.ParseSkip(" DNS, http ", out var skipped, out _) &&
            !skipped.ReverseDns && !skipped.Http && skipped.Ports, "/skip turns off exactly the named checks, in any case and spacing.");
        Require(!KillerScan.Features.Cli.CliRunner.ParseSkip("dns,bogus", out _, out string? skipError) && skipError!.Contains("bogus"),
            "An unknown check name is refused and named.");
        Require(!KillerScan.Features.Cli.CliRunner.ParseSkip("", out _, out _), "An empty /skip is refused.");
        KillerScan.Features.Cli.CliRunner.RecordToLocation(data, "10.1.0.0/24", "10.1.0.0/24", devices, quick: false, err, quiet: true, keyed: false, skipped);
        Require(Equals(data.History.Entries.Last().Options, skipped), "History records the checks a command-line scan skipped.");

        var ethernet = new ScanBinding("Ethernet", System.Net.IPAddress.Parse("192.168.1.5"), 24);
        var wifi = new ScanBinding("Wi-Fi", System.Net.IPAddress.Parse("10.0.0.7"), 24);
        var adapters = new[] { ethernet, wifi };
        Require(KillerScan.Features.Cli.CliRunner.ResolveAdapter("wi-fi", adapters, out _) == wifi &&
            KillerScan.Features.Cli.CliRunner.ResolveAdapter("192.168.1.5", adapters, out _) == ethernet,
            "/adapter finds an adapter by name in any case, or by its address.");
        Require(KillerScan.Features.Cli.CliRunner.ResolveAdapter("Bluetooth", adapters, out string? adapterError) == null &&
            adapterError!.Contains("Ethernet") && adapterError.Contains("Wi-Fi"), "An unknown adapter is refused and the connected ones are listed.");
        var platform = NetworkPlatform.Current;
        try
        {
            // Windows routes 10.8.x.x through a VPN adapter and everything else through Wi-Fi.
            NetworkPlatform.Current = new RoutePlatform();
            var vpn = new ScanBinding("Office VPN", System.Net.IPAddress.Parse("10.8.0.2"), 32);
            Require(KillerScan.Features.Cli.CliRunner.OffLinkTarget(ethernet, [System.Net.IPAddress.Parse("192.168.1.9"), System.Net.IPAddress.Parse("10.0.0.9")], out string? offLink)
                && offLink!.Contains("10.0.0.9") && offLink.Contains("192.168.1.0/24") && offLink.Contains("Wi-Fi"),
                "A target Windows routes through another adapter is refused before scanning, naming that adapter.");
            Require(!KillerScan.Features.Cli.CliRunner.OffLinkTarget(vpn, [System.Net.IPAddress.Parse("10.8.5.20")], out _),
                "A target Windows routes through the bound VPN adapter is allowed.");
        }
        finally { NetworkPlatform.Current = platform; }
        KillerScan.Features.Cli.CliRunner.RecordToLocation(data, "10.1.0.0/24", "10.1.0.0/24", devices, quick: false, err, quiet: true, keyed: false, null, "Ethernet");
        Require(data.History.Entries.Last().Adapter == "Ethernet", "Command-line history records the adapter used.");
    }

    private static void Transfer(string root)
    {
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        catalog.Create("Acme Dental", out var acme);
        catalog.SetNotes(acme!.Id, "Gateway 10.2.0.1\nVLAN 20 is phones");
        acme = catalog.Items.Single(item => item.Id == acme.Id);
        var data = catalog.Open(acme);
        var device = Device("10.2.0.5", "00:00:00:00:02:05");
        data.Devices.SetName(device, "Reception");
        data.Devices.Observe([device]);
        data.Devices.SetTrusted(device, true);
        data.Overrides.Set(device.MacAddress, "Printer");
        data.Logins.Set(device.MacAddress, device.IpAddress, "admin");
        data.SavedScans.Save(new ScanProfile { Name = "Office LAN", Target = "10.2.0.0/24", DeepScanAfter = true });
        data.History.Record("10.2.0.0/24", [device]);

        string withoutUsers = System.Text.Encoding.UTF8.GetString(LocationTransfer.Export(acme, catalog.DirectoryFor(acme), includeUsernames: false));
        Require(!withoutUsers.Contains("admin") && withoutUsers.Contains("Reception") && withoutUsers.Contains("VLAN 20"),
            "Exporting leaves out SSH usernames unless asked, and keeps names and notes.");
        byte[] withUsers = LocationTransfer.Export(acme, catalog.DirectoryFor(acme), includeUsernames: true);
        Require(System.Text.Encoding.UTF8.GetString(withUsers).Contains("admin"), "The checkbox adds SSH usernames.");

        Require(LocationTransfer.Import(catalog, withUsers, out var imported, out string? error) && imported != null, "A Location imports: " + error);
        Require(imported!.Id != acme.Id && imported.Name == "Acme Dental" && imported.Notes.Contains("VLAN 20"),
            "Import always creates a new Location, even with the same name, and keeps its notes.");
        var copy = catalog.Open(imported);
        Require(copy.Devices.GetName(device) == "Reception" && copy.Devices.IsTrusted(device) && copy.Devices.HasBaseline &&
            copy.Overrides.Get(device.MacAddress) == "Printer" && copy.Logins.Get(device.MacAddress, device.IpAddress) == "admin" &&
            copy.SavedScans.Items.Single().DeepScanAfter && copy.History.Entries.Count == 1,
            "The imported Location has the names, approvals, baseline, types, usernames, saved scans and history.");
        copy.Devices.SetName(device, "Changed in the copy");
        Require(data.Devices.GetName(device) == "Reception", "The import is independent of the original.");

        foreach (var (label, json) in new[]
        {
            ("newer version", "{\"Format\":\"KillerScan.Location\",\"Version\":2,\"Name\":\"X\",\"Files\":{}}"),
            ("wrong format", "{\"Format\":\"Something.Else\",\"Version\":1,\"Name\":\"X\",\"Files\":{}}"),
            ("unexpected file", "{\"Format\":\"KillerScan.Location\",\"Version\":1,\"Name\":\"X\",\"Files\":{\"..\\\\evil.json\":{}}}"),
            ("malformed", "{broken"),
            ("null store", "{\"Format\":\"KillerScan.Location\",\"Version\":1,\"Name\":\"X\",\"Files\":{\"devices.json\":null}}"),
            ("number store", "{\"Format\":\"KillerScan.Location\",\"Version\":1,\"Name\":\"X\",\"Files\":{\"profiles.json\":5}}"),
            ("unreadable store", "{\"Format\":\"KillerScan.Location\",\"Version\":1,\"Name\":\"X\",\"Files\":{\"history.json\":{\"Entries\":5}}}"),
        })
        {
            int before = catalog.Items.Count;
            Require(!LocationTransfer.Import(catalog, System.Text.Encoding.UTF8.GetBytes(json), out _, out string? refused) && refused != null &&
                catalog.Items.Count == before, "An import is refused without adding a Location: " + label);
        }
        Require(!File.Exists(Path.Combine(root, "evil.json")) && !File.Exists(Path.Combine(root, "locations", "evil.json")),
            "A refused import writes nothing outside its Location.");
    }

    private static void Suggestions(string root)
    {
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        catalog.Create("Acme Dental", out var acme);
        catalog.Create("Acme Dental (Annex)", out var annex);
        catalog.Create("Shop", out var shop);
        Require(catalog.AddGatewayMac(acme!.Id, "aa-bb-cc-00-00-01") && catalog.AddGatewayMac(annex!.Id, "AA:BB:CC:00:00:01") &&
            catalog.AddGatewayMac(shop!.Id, "AA:BB:CC:00:00:02"), "Gateway MACs are recorded per Location.");
        catalog.AddGatewayMac(shop!.Id, "aa:bb:cc:00:00:02");
        var reloaded = new LocationCatalog(root);
        reloaded.Load("Unassigned");
        Require(reloaded.Items.Single(item => item.Id == shop.Id).GatewayMacs.Count == 1, "The same gateway is recorded once, in any format.");
        Require(LocationCatalog.Matching(reloaded.Items, "AA-BB-CC-00-00-02").Single().Id == shop.Id,
            "A gateway MAC suggests the Location that has seen it.");
        Require(LocationCatalog.Matching(reloaded.Items, "aa:bb:cc:00:00:01").Count == 2,
            "A gateway seen by two Locations suggests both and picks neither.");
        Require(LocationCatalog.Matching(reloaded.Items, "").Count == 0 && LocationCatalog.Matching(reloaded.Items, "AA:BB:CC:00:00:09").Count == 0,
            "No gateway MAC, or an unseen one, suggests nothing.");
    }

    private static void Deletion(string root)
    {
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        catalog.Create("Old site", out var old);
        catalog.Create("Keep", out var keep);
        var data = catalog.Open(old!);
        data.Devices.SetName(Device("10.3.0.1"), "Old router");
        string folder = catalog.DirectoryFor(old!);
        Require(catalog.Delete(old!.Id, out string? kept) && kept != null, "A Location can be deleted.");
        Require(catalog.Items.Single().Id == keep!.Id, "Deleting removes only that Location from the list.");
        Require(!Directory.Exists(folder) && File.ReadAllText(Path.Combine(kept!, "devices.json")).Contains("Old router"),
            "Its files are moved aside, not erased.");
        var reloaded = new LocationCatalog(root);
        reloaded.Load("Unassigned");
        Require(reloaded.Items.Count == 1, "The deletion survives a restart.");
        Require(!reloaded.Delete("no-such-id", out _), "Deleting an unknown Location changes nothing.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RoutePlatform : INetworkPlatform
    {
        public System.Collections.Generic.IReadOnlyDictionary<string, string> ReadNeighborCache() => new System.Collections.Generic.Dictionary<string, string>();
        public string ResolveMac(System.Net.IPAddress address) => "";
        public void FlushDnsCache() { }
        public (string Interface, string NextHop)? BestRoute(System.Net.IPAddress address) =>
            address.ToString().StartsWith("10.8.") ? ("Office VPN", "") : ("Wi-Fi", "10.0.0.1");
    }
}
