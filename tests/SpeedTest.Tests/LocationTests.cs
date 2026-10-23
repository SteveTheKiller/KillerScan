using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Reflection;
using KillerScan.Engine;
using KillerScan.Services;

internal static class LocationTests
{
    internal static async Task RunIsolated()
    {
        using var process = Process.Start(new ProcessStartInfo(typeof(LocationTests).Assembly.Location, "--locations")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => process.WaitForExit(60000))) { process.Kill(); throw new InvalidOperationException("Location fixtures timed out."); }
        Console.Write(await output);
        if (process.ExitCode != 0) throw new InvalidOperationException(await error);
    }
    private static int _checks;
    private static void Require(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Refuses(Action action, string message)
    {
        bool refused = false;
        try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException) { refused = true; }
        Require(refused, message);
    }
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-locations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Migration(Path.Combine(root, "migration"));
        Environment.SetEnvironmentVariable("KILLERSCAN_DATA_ROOT", Path.Combine(root, "scoped"));
        DemoData.Enabled = false;
        KillerScan.App.SetSetting("LocationFixture", "one");
        Require(KillerScan.App.GetSetting("LocationFixture") == "one" && File.Exists(Path.Combine(root, "scoped", "settings.json")),
            "An isolated data root stores app settings in its own file, without sharing registry settings.");
        KillerScan.App.RemoveSetting("LocationFixture");
        Require(KillerScan.App.GetSetting("LocationFixture") == null, "Isolated settings can be removed independently.");
        Isolation();
        Console.WriteLine("PASS: " + _checks + " location identity, migration, isolation, profile and export assertions.");
        Console.WriteLine("Location fixtures: " + root);
    }

    private static void Migration(string root)
    {
        Directory.CreateDirectory(root);
        var samples = new[] { "[]", "{\"Names\":{},\"Trusted\":[]}", "{}", "{}", "[{\"Name\":\"Old\",\"Target\":\"192.0.2.10\"}]" };
        for (int i = 0; i < samples.Length; i++) File.WriteAllText(Path.Combine(root, LocationCatalog.LegacyFiles[i]), samples[i]);
        var catalog = new LocationCatalog(root);
        Require(catalog.Current.Id == "legacy" && catalog.Items.Count == 1, "Old data is assigned only to the explicit legacy namespace.");
        string backup = Directory.GetDirectories(Path.Combine(root, "backups")).Single();
        foreach (string name in LocationCatalog.LegacyFiles)
        {
            byte[] original = File.ReadAllBytes(Path.Combine(root, name));
            Require(original.SequenceEqual(File.ReadAllBytes(Path.Combine(backup, name))), "Backup preserves the original bytes: " + name);
            Require(original.SequenceEqual(File.ReadAllBytes(catalog.DataFile(name))), "Migration copies without modifying original data: " + name);
        }
        _ = new LocationCatalog(root);
        Require(Directory.GetDirectories(Path.Combine(root, "backups")).Length == 1, "Successful migration is not repeated after restart.");
        var site = catalog.Create("Fixture A");
        catalog.Select(site.Id);
        string dataPath = catalog.DataFile("profiles.json");
        catalog.Rename(site.Id, "Renamed A");
        Require(catalog.Current.Id == site.Id && catalog.DataFile("profiles.json") == dataPath, "Renaming never changes identity or paths.");
        Require(new LocationCatalog(root).Current.Id == site.Id, "The last explicit choice persists without any network inference.");
        Refuses(() => catalog.Create("renamed a"), "Duplicate location names are refused.");
        var anotherProcess = new LocationCatalog(root);
        anotherProcess.Select(LocationCatalog.LegacyId);
        Refuses(() => catalog.Create("renamed a"), "A concurrent catalog update still rejects duplicate names.");
        Require(catalog.Current.Id == site.Id, "A failed catalog edit cannot silently switch this process to another process's selection.");
        _ = catalog.Create("Concurrent fixture");
        Require(catalog.Current.Id == site.Id, "A successful catalog edit also preserves the process's explicit selection.");
        Refuses(() => catalog.Create("bad\nname"), "Control characters are refused.");
        Refuses(() => catalog.Select("../other"), "Path traversal is not a location ID.");
        Refuses(() => catalog.Select(Guid.NewGuid().ToString("N")), "An unknown valid-looking ID is not implicitly created.");
        Refuses(() => catalog.DataFile("../profiles.json"), "Data paths accept only known fixed filenames.");
        string manifest = File.ReadAllText(Path.Combine(root, "locations.json"));
        File.WriteAllText(Path.Combine(root, "locations.json"), "{broken");
        Refuses(() => _ = new LocationCatalog(root), "A malformed catalog is not replaced by defaults.");
        Require(File.ReadAllText(Path.Combine(root, "locations.json")) == "{broken", "Corrupt catalog remains intact for recovery.");
        File.WriteAllText(Path.Combine(root, "locations.json"), manifest);

        string broken = root + "-broken";
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "history.json"), "{broken");
        Refuses(() => _ = new LocationCatalog(broken), "Malformed legacy JSON aborts migration.");
        Require(!File.Exists(Path.Combine(broken, "locations.json")) && File.ReadAllText(Path.Combine(broken, "history.json")) == "{broken", "Failed migration publishes no catalog and preserves originals.");
        File.WriteAllText(Path.Combine(broken, "history.json"), "[]");
        Directory.CreateDirectory(Path.Combine(broken, "locations", "legacy"));
        File.WriteAllText(Path.Combine(broken, "locations", "legacy", "history.json"), "[{}]");
        Refuses(() => _ = new LocationCatalog(broken), "An interrupted migration with differing destination data is refused.");
        Require(File.ReadAllText(Path.Combine(broken, "locations", "legacy", "history.json")) == "[{}]", "Interrupted destination is never overwritten.");
        string orphaned = root + "-orphaned";
        Directory.CreateDirectory(Path.Combine(orphaned, "locations", Guid.NewGuid().ToString("N")));
        Refuses(() => _ = new LocationCatalog(orphaned), "A missing catalog cannot hide existing named-location directories behind a fresh catalog.");
        Require(!File.Exists(Path.Combine(orphaned, "locations.json")), "Orphaned location data requires explicit catalog recovery.");
        string interrupted = root + "-interrupted";
        Directory.CreateDirectory(Path.Combine(interrupted, "locations", "legacy"));
        File.WriteAllText(Path.Combine(interrupted, "locations", "legacy", "history.json"), "[]");
        Refuses(() => _ = new LocationCatalog(interrupted), "Interrupted copies absent from the originals are not silently adopted.");
    }

    private static void Isolation()
    {
        var a = Locations.Catalog.Create("Fixture A");
        var b = Locations.Catalog.Create("Fixture B");
        Require(a.Id != b.Id && LocationCatalog.ValidId(a.Id), "Locations have independent opaque identities.");
        Require(Locations.TrySelect(a.Id), "Select location A.");
        var device = new NetworkDevice { IpAddress = "192.0.2.10", MacAddress = "02:00:00:00:00:01" };
        DevicePreferences.SetName(device, "Alpha"); DevicePreferences.SetTrusted(device, true);
        DeviceOverrides.Set(device.MacAddress, "Printer"); DeviceLogins.Set(device.MacAddress, device.IpAddress, "alpha");
        ScanProfiles.Save(new ScanProfile { Name = "Daily", Target = "192.0.2.10", LocationId = a.Id });
        var first = ScanHistory.Record("192.0.2.10", new[] { device });
        Require(first.Previous == null && first.Current.LocationId == a.Id, "The first history entry carries A's identity.");
        var scannerA = EngineHost.CreateScanner();
        var originalProfile = ScanProfiles.Items.Single();
        Require(originalProfile.LocationId == a.Id, "Saved profiles carry location identity.");
        using (var lease = Locations.BeginRun())
        {
            Require(!Locations.TrySelect(b.Id) && Locations.Current.Id == a.Id, "A running scan locks the location at the storage boundary.");
            lease.Dispose(); lease.Dispose();
        }
        Require(!Locations.IsBusy, "Run leases are idempotent.");
        Require(Locations.TrySelect(b.Id), "Select B.");
        Require(DevicePreferences.GetName(device) == null && !DevicePreferences.IsTrusted(device) && !DevicePreferences.HasTrustedDevices,
            "The same IP and MAC do not inherit A's name or trust.");
        Require(DeviceOverrides.Get(device.MacAddress) == null && DeviceLogins.Get(device.MacAddress, device.IpAddress) == null,
            "Manual type and SSH username do not leak between locations.");
        Require(ScanProfiles.Items.Count == 0 && ScanHistory.Entries.Count == 0, "B has its own profiles and history.");
        DevicePreferences.SetName(device, "Beta"); DeviceOverrides.Set(device.MacAddress, "NAS");
        DeviceLogins.Set(device.MacAddress, device.IpAddress, "beta");
        ScanProfiles.Save(new ScanProfile { Name = "Daily", Target = "192.0.2.10", LocationId = b.Id });
        var second = ScanHistory.Record("192.0.2.10", new[] { device });
        Require(second.Previous == null && second.Current.LocationId == b.Id, "Identical targets do not compare across locations.");
        var third = ScanHistory.Record("192.0.2.10", new[] { device });
        Require(third.Previous == second.Current, "Comparison still works inside one location.");
        var snapshot = new NetworkDevice { IpAddress = device.IpAddress, MacAddress = device.MacAddress };
        scannerA.DeviceFinalizer!(snapshot);
        Require(snapshot.Hostname == "Alpha" && snapshot.LocationId == a.Id && scannerA.ManualTypeResolver!(device.MacAddress) == "Printer",
            "An old scanner retains A's immutable preferences and provenance after switching to B.");
        Require(EngineHost.CreateScanner().ManualTypeResolver!(device.MacAddress) == "NAS", "A new scanner captures B's preferences.");
        bool refused = false;
        try { ScanProfiles.Save(originalProfile); } catch (InvalidOperationException) { refused = true; }
        Require(refused && ScanProfiles.Items.Single().LocationId == b.Id, "A stale profile cannot be relabelled and saved into B.");
        refused = false;
        try { ScanHistory.Record("192.0.2.10", new[] { snapshot }); } catch (InvalidOperationException) { refused = true; }
        Require(refused && ScanHistory.Entries.Count == 2, "A late A result cannot be recorded in B's history.");
        DevicePreferences.SetName(snapshot, "wrong"); DevicePreferences.SetTrusted(snapshot, true);
        Require(DevicePreferences.GetName(device) == "Beta" && !DevicePreferences.IsTrusted(device), "A stale A row cannot rename or trust B's device.");
        Require(Locations.TrySelect(a.Id), "Return to A.");
        Require(DevicePreferences.GetName(device) == "Alpha" && DevicePreferences.IsTrusted(device) && DeviceOverrides.Get(device.MacAddress) == "Printer",
            "Names, trust and manual types reload from A's own files.");
        Require(DeviceLogins.Get(device.MacAddress, device.IpAddress) == "alpha" && ScanProfiles.Items.Single().LocationId == a.Id && ScanHistory.Entries.Count == 1,
            "SSH users, profiles and history reload without B's changes.");
        var catalogAgain = new LocationCatalog(Environment.GetEnvironmentVariable("KILLERSCAN_DATA_ROOT")!);
        Require(catalogAgain.Current.Id == a.Id && catalogAgain.Items.Count == 3, "Restart retains independent location identities.");

        var hostileName = new ScanLocation(a.Id, "Test <script> & \"quoted\"");
        string csv = ReportExport.BuildLocationCsv(new[] { device }, hostileName);
        Require(csv.Contains("Location ID,Location") && csv.Contains(a.Id) && csv.Contains("\"\"quoted\"\""), "CSV includes correctly quoted location metadata.");
        string html = ReportExport.BuildLocationHtml(new[] { device }, "192.0.2.10", "dark", hostileName);
        Require(html.Contains(a.Id) && html.Contains("&lt;script&gt;") && !html.Contains("Test <script>"), "HTML includes escaped location metadata.");
        Require(!JsonSerializer.Serialize(device).Contains("LocationId"), "Unhosted engine JSON retains its existing shape.");
        Require(JsonSerializer.Serialize(snapshot).Contains(a.Id), "Hosted JSON preserves location provenance.");
        Require(JsonSerializer.Deserialize<ScanProfile>("{\"Name\":\"old\"}")!.LocationId == "legacy", "Old profiles default only to legacy.");

        var cli = typeof(KillerScan.App).Assembly.GetType("KillerScan.Features.Cli.CliRunner", true)!;
        using (var output = new StringWriter())
        using (var error = new StringWriter())
        {
            int code = (int)cli.GetMethod("RunDevices", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] {
                new List<string> { "192.0.2.10" }, new Dictionary<string, string> { ["/location"] = Guid.NewGuid().ToString("N"), ["/json"] = "" }, output, error, false })!;
            Require(code == 2 && output.ToString() == "" && Locations.Current.Id == a.Id,
                "An unknown CLI location fails before scanning and does not change the active location.");
        }

        string profilesPath = Locations.DataFile("profiles.json");
        string foreign = "[{\"Name\":\"Foreign\",\"LocationId\":\"" + b.Id + "\"}]";
        File.WriteAllText(profilesPath, foreign); ScanProfiles.Load();
        Require(ScanProfiles.Items.Count == 0, "Foreign-location profiles cannot be loaded into the current location.");
        ScanProfiles.Save(new ScanProfile { Name = "do not overwrite", LocationId = a.Id });
        Require(File.ReadAllText(profilesPath) == foreign, "An unreadable or mismatched profile file is preserved.");
        string historyPath = Locations.DataFile("history.json");
        foreign = "[{\"Target\":\"192.0.2.10\",\"LocationId\":\"" + b.Id + "\",\"Devices\":[]}]";
        File.WriteAllText(historyPath, foreign); ScanHistory.Load();
        Require(ScanHistory.Entries.Count == 0 && File.ReadAllText(historyPath) == foreign, "Foreign history is refused before retention can rewrite it.");
        Refuses(() => Locations.Catalog.Select("not-a-site"), "No name/IP/SSID fallback exists for unknown identities.");
    }
}
