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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
