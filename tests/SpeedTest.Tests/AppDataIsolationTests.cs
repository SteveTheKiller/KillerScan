using System;
using System.IO;
using KillerScan.Services;

/// <summary>
/// No test may read or write the user's real KillerScan data. Every app-wide store and the
/// Locations root must resolve inside the scratch folder the runner sets at startup, whatever a
/// test does with demo mode. A test that scanned with demo mode off once appended synthetic runs
/// to the developer's own history.json and devices.json on every suite run.
/// </summary>
internal static class AppDataIsolationTests
{
    internal static void Run()
    {
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerScan");
        foreach (var (name, path) in new[]
        {
            ("history", ScanHistory.HistoryPath),
            ("devices", DevicePreferences.FilePath),
            ("logins", DeviceLogins.FilePath),
            ("overrides", DeviceOverrides.FilePath),
            ("saved scans", ScanProfiles.FilePath),
            ("Locations root", LocationService.Root),
        })
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(real, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The {name} store resolves inside the user's real data folder under test: {full}");
        }
        Console.WriteLine("PASS: every app-wide store and the Locations root resolve outside the user's real data folder.");
    }
}
