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
            BaselineByNetwork(Path.Combine(root, "network"));
            AdoptKeepsTheRun(Path.Combine(root, "adopt"));
            SavedScanRename(Path.Combine(root, "rename"));
            ReloadKeepsUnsaved(Path.Combine(root, "reload"));
            Isolation(Path.Combine(root, "isolation"));
            KnownAndApproved(Path.Combine(root, "known"));
            KnownAcrossProcesses(Path.Combine(root, "processes"));
            CommandLine(Path.Combine(root, "cli"));
            Transfer(Path.Combine(root, "transfer"));
            Suggestions(Path.Combine(root, "suggest"));
            Deletion(Path.Combine(root, "delete"));
            DeletionThatCannotFinish(Path.Combine(root, "stuck"));
            ImportIsPublishedLast(Path.Combine(root, "publish"));
            ImportSurvivesAKill(Path.Combine(root, "kill"));
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

        // Two windows opening together on a clean profile.
        string together = Path.Combine(root, "together");
        var starts = System.Linq.Enumerable.Range(0, 2).Select(_ => System.Threading.Tasks.Task.Run(() =>
        {
            var window = new LocationCatalog(together);
            window.Load("Unassigned");
            return window.LoadError == null ? window.Items.Count : -1;
        })).ToArray();
        System.Threading.Tasks.Task.WaitAll(starts);
        Require(starts.All(start => start.Result == 0), "Two first starts each find an empty, working catalog.");
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
        var held = new System.Threading.ManualResetEventSlim();
        var release = new System.Threading.ManualResetEventSlim();
        var holder = new System.Threading.Thread(() =>
        {
            using var lease = KillerScan.Services.ProfileLock.Acquire("Preferences", path, TimeSpan.FromSeconds(2), "busy");
            held.Set();
            release.Wait();
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
        history.Adopt([temporary.Entries.Single()]);
        System.Threading.Thread.Sleep(20);
        var latest = history.Record("10.3.0.0/24", "10.3.0.0/24", [Device("10.3.0.2")], ScanCompletion.Complete, new ScanOptions(), deep: false);
        Require(latest.Previous?.ScannedAt == newer.ScannedAt, "The baseline is the newest earlier run, not the one adopted last.");
        Require(history.Compare(history.Entries.Last()).Previous?.ScannedAt == newer.ScannedAt, "Viewing the run later picks the same baseline.");
    }

    private static HistoryStore Reread(string path)
    {
        var store = new HistoryStore(path);
        store.Load();
        return store;
    }

    // Two sites can share an address range. A run is compared only with one the evidence puts on
    // the same network, and a gap in the evidence means no comparison rather than a wrong one.
    private static void BaselineByNetwork(string root)
    {
        const string Targets = "192.168.1.0/24";
        ScanNetwork Office = new("AA:AA:AA:00:00:01", "adapter-1"), Plant = new("BB:BB:BB:00:00:02", "adapter-2");
        int store = 0;
        HistoryStore Fresh()
        {
            var history = new HistoryStore(Path.Combine(root, (++store).ToString(), "history.json"));
            history.Load();
            return history;
        }
        ScanComparison Run(HistoryStore history, ScanNetwork network, string adapter = "Ethernet")
        {
            var result = history.Record(Targets, Targets, [Device("192.168.1.10")], ScanCompletion.Complete, new ScanOptions(), false, adapter, network);
            System.Threading.Thread.Sleep(2);
            return result;
        }

        var overlap = Fresh();
        var first = Run(overlap, Office);
        Require(first.Previous == null && Run(overlap, Plant).Previous == null, "Two networks on one address range are never compared.");
        Require(Run(overlap, Office).Previous?.ScannedAt == first.Current.ScannedAt, "A run finds its own network's last run, even with another's in between.");

        var renamed = Fresh();
        Run(renamed, Office, "Ethernet");
        Require(Run(renamed, Office, "Office LAN").Previous != null, "Renaming the adapter changes nothing.");

        var unreadRouter = Fresh();
        Run(unreadRouter, Office);
        Require(Run(unreadRouter, new ScanNetwork("", "adapter-1")).Previous != null, "A gateway missing from the ARP cache falls back on the same adapter.");

        var elsewhere = Fresh();
        Run(elsewhere, new ScanNetwork("", "other-pc"));
        Require(Run(elsewhere, new ScanNetwork("", "this-pc"), "Ethernet").Previous == null, "The same adapter name on another PC is not the same network.");
        Run(elsewhere, new ScanNetwork(Office.GatewayMac, "other-pc"));
        Require(Run(elsewhere, new ScanNetwork(Office.GatewayMac, "this-pc")).Previous != null, "The same router seen from another PC is the same network.");

        var legacy = Fresh();
        Require(legacy.Record(Targets, Targets, [Device("192.168.1.10")], ScanCompletion.Complete, new ScanOptions(), false).Previous == null, "A run with no evidence starts.");
        System.Threading.Thread.Sleep(2);
        Require(legacy.Record(Targets, Targets, [Device("192.168.1.10")], ScanCompletion.Complete, new ScanOptions(), false).Previous != null, "Older runs with no evidence still compare with each other.");
        System.Threading.Thread.Sleep(2);
        Require(Run(legacy, Office).Previous == null, "A run with evidence does not assume an older run was on its network.");

        // The evidence travels with the run, through the file and a move to another Location.
        var saved = Reread(overlap.HistoryPath);
        Require(saved.Entries[0].GatewayMac == Office.GatewayMac && saved.Entries[0].AdapterId == Office.AdapterId, "The evidence reaches the file.");
        var moved = Fresh();
        Require(moved.Adopt([saved.Entries[0]]) == AdoptResult.Saved && Reread(moved.HistoryPath).Entries[0].GatewayMac == Office.GatewayMac, "Moving a run keeps its evidence.");
        Require(Run(moved, Office).Previous != null, "A moved run can still be a baseline.");
    }

    // Adopting succeeds only when the run itself is in the file. A run the destination's retention
    // would drop at once is refused, so the caller keeps its own copy.
    private static void AdoptKeepsTheRun(string root)
    {
        ScanHistoryEntry Run(string target, DateTimeOffset at) => new() { Target = target, ScannedAt = at, Completion = ScanCompletion.Complete };
        var now = DateTimeOffset.Now;

        string countPath = Path.Combine(root, "count", "history.json");
        var count = new HistoryStore(countPath);
        count.Load();
        Require(count.ApplyRetention(new HistoryRetention(HistoryRetentionMode.Count, 1), _ => true), "The destination keeps one run.");
        Require(count.Adopt([Run("newer", now)]) == AdoptResult.Saved, "A run that fits is saved.");
        byte[] before = File.ReadAllBytes(countPath);
        Require(count.Adopt([Run("older", now.AddDays(-1))]) == AdoptResult.NotRetained, "An older run the count would drop is refused.");
        Require(File.ReadAllBytes(countPath).SequenceEqual(before) && count.Entries.Single().Target == "newer" && Reread(countPath).Entries.Single().Target == "newer",
            "A refused run changes nothing.");
        Require(count.Adopt([Run("newest", now.AddDays(1))]) == AdoptResult.Saved && Reread(countPath).Entries.Single().Target == "newest",
            "A newer run replaces the oldest, as any new run does.");

        string timePath = Path.Combine(root, "time", "history.json");
        var time = new HistoryStore(timePath);
        time.Load();
        Require(time.ApplyRetention(new HistoryRetention(HistoryRetentionMode.Time, Days: 7), _ => true), "The destination keeps a week.");
        Require(time.Adopt([Run("stale", now.AddDays(-30))]) == AdoptResult.NotRetained && Reread(timePath).Entries.Count == 0, "A run older than the time limit is refused.");
        Require(time.Adopt([Run("recent", now.AddDays(-1))]) == AdoptResult.Saved && Reread(timePath).Entries.Single().Target == "recent", "A run inside the time limit is saved.");

        string pathAll = Path.Combine(root, "all", "history.json");
        var all = new HistoryStore(pathAll);
        all.Load();
        Require(all.ApplyRetention(new HistoryRetention(HistoryRetentionMode.Count, 2), _ => true), "The destination keeps two runs.");
        Require(all.Adopt([Run("first", now), Run("second", now.AddMinutes(1)), Run("third", now.AddMinutes(2))]) == AdoptResult.NotRetained
            && Reread(pathAll).Entries.Count == 0, "Runs that do not all fit add nothing, so a move never lands in part.");

        var once = Run("once", now);
        Require(all.Adopt([once]) == AdoptResult.Saved && all.Adopt([once]) == AdoptResult.Saved && Reread(pathAll).Entries.Count == 1,
            "Adopting the same run again adds no second copy.");

        var blocked = Run("blocked", now.AddMinutes(1));
        using (new FileStream(pathAll, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Require(all.Adopt([blocked]) == AdoptResult.Failed && all.SaveError != null, "A write that fails is reported as failed.");
            Require(all.Entries.Single().Target == "once", "A failed adoption shows what is really saved.");
        }
        Require(Reread(pathAll).Entries.Single().Target == "once" && all.Retry() && Reread(pathAll).Entries.Count == 1,
            "A failed adoption leaves nothing behind for a later save to write.");
        Require(all.Adopt([blocked]) == AdoptResult.Saved && Reread(pathAll).Entries.Count == 2, "The same run is saved once the file is free.");
    }

    // A rename reaches the file, measured the way the menu calls it: with the object from the list.
    private static void SavedScanRename(string root)
    {
        string path = Path.Combine(root, "profiles.json");
        SavedScanStore Fresh()
        {
            var opened = new SavedScanStore(path);
            opened.Load();
            return opened;
        }
        var store = new SavedScanStore(path);
        Require(store.Save(new ScanProfile { Name = "Original", Target = "10.0.0.0/24", DeepScanAfter = true, Adapter = "Ethernet" }), "A scan is saved.");
        Require(store.Save(new ScanProfile { Name = "Other", Target = "10.0.1.0/24" }), "A second scan is saved.");

        Require(store.Rename(store.Items.First(item => item.Name == "Original"), "Renamed", out bool taken) && !taken, "The rename reports success.");
        var reread = Fresh().Items.Single(item => item.Target == "10.0.0.0/24");
        Require(reread.Name == "Renamed" && reread.DeepScanAfter && reread.Adapter == "Ethernet", "The new name reaches the file with everything else the scan holds.");
        Require(store.Items.Any(item => item.Name == "Renamed"), "The list shows the new name.");

        Require(store.Rename(store.Items.First(item => item.Name == "Renamed"), "RENAMED", out taken) && !taken
                && Fresh().Items.Any(item => item.Name == "RENAMED"), "A case-only rename reaches the file.");

        Require(!store.Rename(store.Items.First(item => item.Name == "RENAMED"), "other", out taken) && taken
                && Fresh().Items.Count(item => item.Target == "10.0.0.0/24" && item.Name == "RENAMED") == 1, "A name in use is refused.");

        // Another window took the name since this one loaded. Only the file knows that.
        var otherWindow = Fresh();
        Require(otherWindow.Rename(otherWindow.Items.First(item => item.Name == "Other"), "Taken", out _), "Another window renames its scan.");
        Require(!store.Rename(store.Items.First(item => item.Name == "RENAMED"), "taken", out taken) && taken, "A name another window just took is refused.");
        Require(Fresh().Items.Count == 2 && Fresh().Items.Any(item => item.Name == "RENAMED") && Fresh().Items.Any(item => item.Name == "Taken"), "Neither scan changed.");

        var selected = store.Items.First(item => item.Name == "RENAMED");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Require(!store.Rename(selected, "After retry", out taken) && !taken && store.SaveError != null, "A blocked file reports the failure.");
        Require(selected.Name == "RENAMED" && store.Items.Any(item => item.Name == "RENAMED"), "A failed rename shows what is really saved.");
        Require(store.Rename(selected, "After retry", out taken) && Fresh().Items.Any(item => item.Name == "After retry" && item.DeepScanAfter),
            "Retrying with the same selected object reaches the file.");

        var deleted = Fresh();
        Require(deleted.Delete(deleted.Items.First(item => item.Name == "After retry")), "Another window deletes the scan.");
        Require(!store.Rename(store.Items.First(item => item.Name == "After retry"), "Gone", out taken) && !taken && store.SaveError == null,
            "Renaming a scan that no longer exists does not claim success.");
        Require(Fresh().Items.Single().Name == "Taken", "Nothing comes back.");
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

    /// <summary>The other process in <see cref="KnownAcrossProcesses"/>: one scan seeing one device.</summary>
    internal static void ObserveOnce(string path, string mac) => OpenDevices(path).Observe([Device("10.9.0.1", mac)]);

    private static DevicePreferenceStore OpenDevices(string path)
    {
        var store = new DevicePreferenceStore(path);
        store.Load();
        return store;
    }

    // Two KillerScans open the same new Location before either has scanned. What counts as new is
    // decided by the file, so the second scan still alerts on what the first never saw, and
    // neither loses what the other recorded.
    private static void KnownAcrossProcesses(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "devices.json");
        NetworkDevice first = Device("10.9.0.1", "00:00:00:00:09:01"), second = Device("10.9.0.2", "00:00:00:00:09:02");
        var stale = OpenDevices(path);
        var writer = OpenDevices(path);
        Require(!stale.HasBaseline, "Neither window has a baseline yet.");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(typeof(LocationTests).Assembly.Location,
            "--observe-writer \"" + path + "\" " + first.MacAddress) { UseShellExecute = false, CreateNoWindow = true })!)
            Require(process.WaitForExit(15000) && process.ExitCode == 0, "The other KillerScan finishes its first scan.");

        var seen = stale.Observe([first, second]);
        Require(!seen.BaselineCreated && seen.NewDevices.Single() == second, "The second scan flags the device the baseline never saw.");
        Require(stale.HasBaseline && stale.IsKnown(first) && stale.IsKnown(second), "This window now knows both.");
        var reread = OpenDevices(path);
        Require(reread.IsKnown(first) && reread.IsKnown(second), "Neither scan lost the other's devices.");

        // An approval revoked in another window is not shown as current after the next scan.
        Require(writer.SetTrusted(first, true) && stale.Observe([first]).Saved, "Another window approves a device.");
        Require(stale.IsTrusted(first), "This window picks the approval up.");
        Require(writer.SetTrusted(first, false) && stale.Observe([first]).Saved, "Another window revokes it.");
        Require(!stale.IsTrusted(first), "The revocation is picked up by the next scan.");

        // A file that cannot be written still gives an answer, keeps the devices for a retry, and does not lose them.
        var third = Device("10.9.0.3", "00:00:00:00:09:03");
        Observation blocked;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            blocked = stale.Observe([first, second, third]);
        Require(!blocked.Saved && blocked.NewDevices.Single() == third && !blocked.BaselineCreated, "A blocked file still flags the new device.");
        Require(stale.Retry() && OpenDevices(path).IsKnown(third), "The retry saves it.");

        // A file unreadable at the scan and before it: no baseline is claimed over what is really there,
        // and the devices stay unseen, so the next readable scan still flags them.
        var fourth = Device("10.9.0.4", "00:00:00:00:09:04");
        Observation unreadable;
        DevicePreferenceStore blind;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            blind = OpenDevices(path);
            unreadable = blind.Observe([first, fourth]);
        }
        Require(!unreadable.Saved && !unreadable.BaselineCreated && unreadable.NewDevices.Count == 0, "An unreadable file claims no baseline: " + unreadable);
        var later = OpenDevices(path).Observe([first, fourth]);
        Require(later.NewDevices.Single() == fourth, "The next readable scan flags the device the unreadable one could not judge.");
    }

    // /locations lists what is there and changes nothing, even on a profile not yet moved into Locations.
    private static void Listing(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "history.json"), "{\"Entries\":[]}");
        string Snapshot() => string.Join("|", Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => path + ":" + (File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks + ":" + new FileInfo(path).Length : "folder")));
        (int Code, string Out, string Err) List(params string[] words)
        {
            var output = new StringWriter();
            var errors = new StringWriter();
            var options = words.Where(word => word.StartsWith("/", StringComparison.Ordinal)).ToDictionary(word => word, _ => "", StringComparer.OrdinalIgnoreCase);
            int code = KillerScan.Features.Cli.CliRunner.RunLocations(words.Where(word => !word.StartsWith("/", StringComparison.Ordinal)).ToList(), options, output, errors);
            return (code, output.ToString().Trim(), errors.ToString().Trim());
        }

        string previous = LocationService.Root;
        try
        {
            LocationService.Root = root;
            string untouched = Snapshot();
            var unmoved = List("/json");
            Require(unmoved.Code == 0 && unmoved.Out == "[]", "A profile not yet moved into Locations lists none: " + unmoved.Code + " " + unmoved.Out + unmoved.Err);
            Require(Snapshot() == untouched, "Listing moves nothing into Locations.");
            Require(List().Out.Contains("No Locations"), "The table says when there are none.");

            var catalog = new LocationCatalog(root);
            catalog.Load("Unassigned");
            catalog.Create("Zephyr Clinic", "Zephyr Health", "The alarm code is 4417.", out var zephyr);
            catalog.Create("Acme \u001b[2JDental", out var acme);
            catalog.SetLastUsed(zephyr!.Id, "10.1.0.0/24", new KillerScan.Engine.ScanOptions(), "Ethernet 2");
            string saved = Snapshot();

            var listed = List("/json");
            Require(listed.Code == 0, "Listing Locations succeeds: " + listed.Err);
            using (var document = System.Text.Json.JsonDocument.Parse(listed.Out))
            {
                var rows = document.RootElement.EnumerateArray().ToList();
                var unassigned = rows.Single(row => row.GetProperty("id").GetString() == LocationCatalog.UnassignedId);
                Require(unassigned.GetProperty("name").GetString() == "Unassigned", "The original data is listed as Unassigned.");
                Require(rows.Count == 3, "Every Location is listed.");
                var row = rows.Single(item => item.GetProperty("id").GetString() == zephyr.Id);
                Require(row.GetProperty("name").GetString() == "Zephyr Clinic" && row.GetProperty("customer").GetString() == "Zephyr Health"
                    && row.GetProperty("lastTargets").GetString() == "10.1.0.0/24" && row.GetProperty("lastAdapter").GetString() == "Ethernet 2",
                    "A Location lists what a scan needs to use it.");
                Require(rows.Single(item => item.GetProperty("id").GetString() == acme!.Id).GetProperty("name").GetString() == "Acme \u001b[2JDental",
                    "JSON keeps a name exactly as it was given.");
                Require(rows.Select(item => item.GetProperty("name").GetString()).SequenceEqual(rows.Select(item => item.GetProperty("name").GetString()).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
                    "Locations list in name order.");
            }
            Require(!listed.Out.Contains("4417") && !listed.Out.Contains("alarm"), "Free-text notes stay out of the list.");
            Require(!listed.Out.Contains("GatewayMacs"), "The networks a Location has seen stay out of the list.");

            var table = List();
            Require(table.Code == 0 && table.Out.Contains("Zephyr Clinic") && table.Out.Contains(zephyr.Id), "The table names each Location and its ID.");
            Require(!table.Out.Contains("\u001b"), "A Location name cannot send terminal control codes.");
            Require(List("nearby").Code == 2, "Listing takes no targets.");
            Require(List("/quiet").Code == 2, "Listing takes only /json.");
            Require(Snapshot() == saved, "Listing changes nothing on disk.");

            string path = Path.Combine(root, "locations.json");
            byte[] good = File.ReadAllBytes(path);
            File.WriteAllText(path, "not json");
            var unreadable = List("/json");
            Require(unreadable.Code == 1 && unreadable.Err.Contains("Locations are unavailable"), "An unreadable catalog is a failure, not an empty list.");
            Require(File.ReadAllText(path) == "not json", "An unreadable catalog is left as it was.");
            File.WriteAllBytes(path, good);
        }
        finally { LocationService.Root = previous; }
    }

    private static void CommandLine(string root)
    {
        Listing(Path.Combine(root, "listing"));
        // uiscan is a command only at the front. Later it is a value, and a value must not open the app.
        foreach (string[] words in new[]
        {
            new[] { "/scan", "10.0.0.0/24", "/json", "/location", "uiscan" }, new[] { "/probe", "10.0.0.1", "/adapter", "--uiscan" },
            new[] { "scan", "/filter", "/uiscan" }, new[] { "/scan", "10.0.0.1" }, new string[0],
        })
            Require(KillerScan.Features.Cli.CliRunner.UiScanIndex(words) == -1, "A value named uiscan stays a value: " + string.Join(" ", words));
        Require(KillerScan.Features.Cli.CliRunner.UiScanIndex(new[] { "uiscan", "10.0.0.1" }) == 0
            && KillerScan.Features.Cli.CliRunner.UiScanIndex(new[] { "/UISCAN" }) == 0
            && KillerScan.Features.Cli.CliRunner.UiScanIndex(new[] { "--uiscan", "10.0.0.1" }) == 0, "uiscan at the front opens the interface.");
        Require(KillerScan.Features.Cli.CliRunner.UiScanIndex(new[] { "--demo", "/uiscan" }) == 1, "The demo switch can come before it.");
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

        // One device reads as one device, in the terminal, the command line and the status bar.
        var single = new[] { Device("10.2.0.1", "00:00:00:00:02:01") };
        catalog.Create("Keyed", out var keyedSite);
        var one = new StringWriter();
        KillerScan.Features.Cli.CliRunner.RecordToLocation(catalog.Open(keyedSite!), "10.2.0.1", "10.2.0.1", single, quick: false, one, quiet: false, keyed: true);
        Require(one.ToString().Contains("Str_St_KnownBaselineOne|1"), "The in-app terminal gets the singular baseline text: " + one);
        catalog.Create("Plain", out var plainSite);
        var plain = catalog.Open(plainSite!);
        one = new StringWriter();
        KillerScan.Features.Cli.CliRunner.RecordToLocation(plain, "10.2.0.1", "10.2.0.1", single, quick: false, one, quiet: false, keyed: false);
        Require(one.ToString().Contains("Baseline set: 1 device is now known. It is not approved yet."), "The command line says one device: " + one);
        one = new StringWriter();
        KillerScan.Features.Cli.CliRunner.RecordToLocation(plain, "10.2.0.0/30", "10.2.0.0/30", [single[0], Device("10.2.0.2", "00:00:00:00:02:02")], quick: false, one, quiet: false, keyed: false);
        Require(one.ToString().Contains("Alert: 1 device is new since the baseline."), "The command line says one new device: " + one);
        Require(ScanStatusText.Counted("Str_Count_Found", 1) == "Str_Count_FoundOne" && ScanStatusText.Counted("Str_Count_Found", 0) == "Str_Count_Found"
            && ScanStatusText.Counted("Str_Count_Found", 2) == "Str_Count_Found", "Only a count of exactly one takes the singular string.");
        Require(ScanStatusText.Protocol(new ScanStatus(ScanStage.Complete, 1)) == "Str_St_ScanCompleteOne|1"
            && ScanStatusText.Protocol(new ScanStatus(ScanStage.Complete, 3)) == "Str_St_ScanComplete|3", "A finished scan of one device uses the singular status.");
        foreach (string file in Directory.GetFiles(Path.Combine(TestRepository.Find().FullName, "Strings"), "*.xaml"))
        {
            string text = File.ReadAllText(file);
            foreach (string key in new[] { "Str_Count_FoundOne", "Str_St_ScanCompleteOne", "Str_St_KnownBaselineOne", "Str_St_UnknownDevicesOne" })
                Require(text.Contains("x:Key=\"" + key + "\">") && text.Substring(text.IndexOf("x:Key=\"" + key + "\">", StringComparison.Ordinal)).Split('\n')[0].Contains("{0}"),
                    Path.GetFileName(file) + " has " + key + " with its count.");
        }
        err = new StringWriter();
        KillerScan.Features.Cli.CliRunner.RecordToLocation(data, "10.1.0.0/24", "10.1.0.0/24",
            [.. devices, Device("10.1.0.3", "00:00:00:00:01:03")], quick: false, err, quiet: false, keyed: false);
        Require(err.ToString().Contains("1 device is new"), "A later command-line scan reports devices new since the baseline.");
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

    // Deleting either finishes or changes nothing, and nothing it reports is untrue. A folder that
    // cannot be set aside stops the delete, so no files are left behind with no list pointing at them.
    private static void DeletionThatCannotFinish(string root)
    {
        LocationCatalog Reloaded()
        {
            var catalog = new LocationCatalog(root);
            catalog.Load("Unassigned");
            return catalog;
        }
        var catalog = Reloaded();
        Require(catalog.Create("Site", out var site), "A Location is created.");
        var data = catalog.Open(site!);
        Require(data.Devices.SetName(Device("10.6.0.1"), "Router"), "It has data.");
        string folder = catalog.DirectoryFor(site!), deleted = Path.Combine(root, "locations", ".deleted");
        void StillThere(string message) => Require(File.Exists(Path.Combine(folder, "devices.json")) && Reloaded().Items.Any(item => item.Id == site!.Id), message);

        // A file where the recovery folder belongs.
        File.WriteAllText(deleted, "not a folder");
        Require(!catalog.Delete(site!.Id, out string? kept) && kept == null && catalog.SaveError != null, "No recovery folder stops the delete and says why.");
        StillThere("A delete that could not start changes nothing.");
        File.Delete(deleted);

        // A file in the Location is held open.
        using (new FileStream(Path.Combine(folder, "devices.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Require(!catalog.Delete(site.Id, out kept) && kept == null && catalog.SaveError != null, "A locked file stops the delete and says why.");
        StillThere("A locked Location stays listed, with its files where they were.");

        // The catalog cannot be written once the folder is aside: it goes back.
        using (new FileStream(Path.Combine(root, "locations.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Require(!catalog.Delete(site.Id, out kept) && kept == null, "A catalog that cannot be written stops the delete.");
        StillThere("The folder is put back when the catalog cannot change.");
        Require(!Directory.Exists(deleted) || !Directory.EnumerateFileSystemEntries(deleted).Any(), "Nothing is left in the recovery folder.");

        // A delete that failed does not speak for a later failure that has its own reason.
        using (new FileStream(Path.Combine(folder, "devices.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Require(!catalog.Delete(site.Id, out kept), "A locked file stops the delete.");
        string deleteReason = catalog.SaveError!;
        using (new FileStream(Path.Combine(root, "locations.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Require(!catalog.Rename(site.Id, "New name") && catalog.SaveError != deleteReason, "A later failure reports its own reason.");

        // Everything clear: the same delete finishes, and the path it reports is real.
        Require(catalog.Delete(site.Id, out kept) && kept != null && File.Exists(Path.Combine(kept, "devices.json")), "The delete finishes and reports where the files went.");
        Require(catalog.SaveError == null, "The earlier failure is not reported after a success.");
        Require(!Directory.Exists(folder) && !Reloaded().Items.Any(item => item.Id == site.Id), "The Location is gone after a restart.");

        // A Location that never saved anything has no folder to move and reports none.
        Require(catalog.Create("Empty", out var empty) && catalog.Delete(empty!.Id, out kept) && kept == null, "A Location with no folder deletes.");
    }

    private static readonly byte[] ImportFile = System.Text.Encoding.UTF8.GetBytes(
        "{\"Format\":\"KillerScan.Location\",\"Version\":1,\"Name\":\"Imported\",\"Customer\":\"Acme\",\"Notes\":\"n\",\"Icon\":\"\"," +
        "\"Files\":{\"profiles.json\":[{\"Name\":\"Office\",\"Target\":\"10.7.0.0/24\"}]}}");

    private static bool HasCompleteImport(LocationCatalog catalog, string root) =>
        catalog.Items.Any(item => item.Name == "Imported" && File.Exists(Path.Combine(root, "locations", item.Id, "profiles.json")));

    // An entry is listed only once its files are in place. Listed and empty is the one state an
    // import must never be seen in, by this process or another.
    private static void ImportIsPublishedLast(string root)
    {
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        var move = LocationTransfer.MoveDirectory;
        try
        {
            bool listedDuringMove = true;
            LocationTransfer.MoveDirectory = (from, to) =>
            {
                listedDuringMove = new Func<bool>(() =>
                {
                    var other = new LocationCatalog(root);
                    other.Load("Unassigned");
                    return other.Items.Any(item => item.Name == "Imported");
                })();
                Directory.Move(from, to);
            };
            Require(LocationTransfer.Import(catalog, ImportFile, out _, out _) && !listedDuringMove, "Another window never sees the entry before its files.");
        }
        finally { LocationTransfer.MoveDirectory = move; }
        Require(HasCompleteImport(catalog, root), "The import is complete.");

        // The catalog refuses the entry after the files moved: the files leave again and the retry works.
        int before = catalog.Items.Count;
        using (new FileStream(Path.Combine(root, "locations.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Require(!LocationTransfer.Import(catalog, ImportFile, out var refused, out string? error) && refused == null && !string.IsNullOrEmpty(error), "A catalog that cannot be written fails the import.");
        Require(catalog.Items.Count == before, "Nothing is listed.");
        var folders = Directory.GetDirectories(Path.Combine(root, "locations")).Select(Path.GetFileName).ToList();
        Require(folders.Count == before, "No folder is left behind: " + string.Join(", ", folders));
        Require(LocationTransfer.Import(catalog, ImportFile, out var again, out _) && File.Exists(Path.Combine(catalog.DirectoryFor(again!), "profiles.json")), "The same import works once the catalog is free.");
    }

    /// <summary>The child in <see cref="ImportSurvivesAKill"/>: an import stopped dead by a killed process.</summary>
    internal static void ImportThenDie(string root, string boundary)
    {
        var catalog = new LocationCatalog(root);
        catalog.Load("Unassigned");
        LocationTransfer.MoveDirectory = (from, to) =>
        {
            if (boundary == "after-move") Directory.Move(from, to);
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        };
        LocationTransfer.Import(catalog, ImportFile, out _, out _);
    }

    private static void ImportSurvivesAKill(string root)
    {
        foreach (string boundary in new[] { "before-move", "after-move" })
        {
            string site = Path.Combine(root, boundary);
            Directory.CreateDirectory(site);
            using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(typeof(LocationTests).Assembly.Location,
                "--import-then-die \"" + site + "\" " + boundary) { UseShellExecute = false, CreateNoWindow = true })!)
                Require(process.WaitForExit(15000) && process.ExitCode != 0, "The importing process dies: " + boundary);
            var catalog = new LocationCatalog(site);
            catalog.Load("Unassigned");
            Require(catalog.Items.All(item => item.Name != "Imported"), "A killed import lists nothing half-made: " + boundary);
            Require(LocationTransfer.Import(catalog, ImportFile, out var imported, out string? error) && HasCompleteImport(catalog, site),
                "The same import works afterward: " + boundary + " " + error);
            Require(catalog.Items.Count(item => item.Name == "Imported") == 1, "It is listed once: " + boundary);
        }
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
