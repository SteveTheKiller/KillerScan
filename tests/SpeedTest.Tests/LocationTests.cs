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
            Isolation(Path.Combine(root, "isolation"));
            KnownAndApproved(Path.Combine(root, "known"));
            CommandLine(Path.Combine(root, "cli"));
            Transfer(Path.Combine(root, "transfer"));
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
        })
        {
            int before = catalog.Items.Count;
            Require(!LocationTransfer.Import(catalog, System.Text.Encoding.UTF8.GetBytes(json), out _, out string? refused) && refused != null &&
                catalog.Items.Count == before, "An import is refused without adding a Location: " + label);
        }
        Require(!File.Exists(Path.Combine(root, "evil.json")) && !File.Exists(Path.Combine(root, "locations", "evil.json")),
            "A refused import writes nothing outside its Location.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
