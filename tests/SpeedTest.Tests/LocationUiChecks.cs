using System;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Services;
using KillerScan.Shell;

internal static class LocationUiChecks
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static void Run(MainWindow window, ScanWorkspace workspace, Action capture)
    {
        Require(DemoData.Enabled, "Location UI checks use fabricated data only.");
        var change = typeof(MainWindow).GetMethod("ChangeLocation", Instance)!;
        bool Select(string id) => (bool)change.Invoke(window, new object[] { id })!;
        var a = Locations.Catalog.Create("Fixture A"); var b = Locations.Catalog.Create("Fixture B");
        // No confirmation dialog is needed when the workspace has no transient results.
        workspace.Session.Devices.Clear();
        Require(Select(a.Id) && workspace.LocationId == a.Id, "The shell changes the scan session's location.");
        workspace.Targets = "192.0.2.10";
        workspace.LoadProfile(new ScanProfile { Name = "B profile", LocationId = b.Id, Target = "198.51.100.10" });
        Require(workspace.Targets == "192.0.2.10", "A foreign profile cannot replace the current target.");
        workspace.RunProfile(new ScanProfile { Name = "B profile", LocationId = b.Id, Target = "198.51.100.10" });
        Require(workspace.Session.Devices.Count == 0, "A foreign profile cannot start a demo scan.");
        using (Locations.BeginRun()) Require(!Select(b.Id) && workspace.LocationId == a.Id, "The shell also refuses a location switch during a run.");
        workspace.SelectInterface("fixture-adapter", "192.0.2.20", false);
        typeof(MainWindow).GetField("_selectedWorkspace", Instance)!.SetValue(window, new System.Windows.Controls.Grid());
        Require(Select(b.Id) && workspace.Targets == "" && workspace.Session.Devices.Count == 0 && workspace.SelectedAdapterId == "",
            "Changing location clears results, targets and explicit interface identity.");
        Require(((Button)window.FindName("SaveProfileButton")).IsEnabled, "Returning from another view enables saving profiles for the new location.");
        var title = (TextBlock)window.FindName("SubtitleText");
        Require(title.Text.Contains("Fixture B"), "The current location is visible in the window title bar.");
        var button = (Button)typeof(MainWindow).GetField("_locationButton", Instance)!.GetValue(window)!;
        Require(button.ToolTip!.ToString()!.Contains("Fixture B"), "The picker identifies the active location.");
        Locations.Catalog.Rename(b.Id, "Renamed B");
        typeof(MainWindow).GetMethod("RefreshLocationLabel", Instance)!.Invoke(window, null);
        Require(workspace.LocationId == b.Id && title.Text.Contains("Renamed B"), "Renaming changes the label while preserving identity.");
        capture();
        Require(Select(LocationCatalog.LegacyId), "The legacy namespace remains explicitly selectable.");
        workspace.Scan();
        Console.WriteLine("PASS: location picker, visible identity, profile refusal, run locking and transient-state reset.");
    }
}
