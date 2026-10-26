using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Services;
using KillerScan.Shell;

// Manage Locations shows every Location in the sidebar, marks the current one, and opens one
// with a click or Enter. Runs inside the history suite's host, which points Locations at a
// scratch folder before the window loads them.
internal static class LocationsSidebarTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    internal static void Run()
    {
        // Demo mode skips Locations entirely; this check needs the real path, on scratch data.
        DemoData.Enabled = false;
        var window = new MainWindow();
        Require(LocationService.Loaded && LocationService.Editable && LocationService.Items.Count == 0,
            "The window loads an empty scratch catalog, never the real one.");
        LocationService.Create("Acme Dental", out var acme);
        LocationService.Create("Branch office", out var branch);
        LocationService.SetNotes(branch!, "\nGateway 10.9.0.1\nVLAN 30");

        Call(window, "NewScan", "");
        var workspace = (KillerScan.Controls.ScanWorkspace)typeof(MainWindow).GetField("_scanWorkspace", Instance)!.GetValue(window)!;
        workspace.OpenLocation(acme!);
        Call(window, "ShowLocationsSection");
        var list = (ListBox)window.FindName("LocationsList");
        var heading = (TextBlock)window.FindName("SidebarHeading");
        Require(list.Visibility == Visibility.Visible && heading.Text == (string)Application.Current.FindResource("Str_Loc_ManageTitle"),
            "Manage Locations shows the Locations section under its own heading.");
        Require(((Button)window.FindName("NewLocationButton")).Visibility == Visibility.Visible,
            "The section offers New Location.");
        var rows = list.Items.Cast<object>().ToList();
        string Name(object row) => (string)row.GetType().GetProperty("Name")!.GetValue(row)!;
        string Detail(object row) => (string)row.GetType().GetProperty("Detail")!.GetValue(row)!;
        Require(rows.Count == 2 && Name(rows[0]) == string.Format((string)Application.Current.FindResource("Str_Loc_Current"), "Acme Dental"),
            "Every Location is listed and the current one is marked.");
        Require(Detail(rows[1]) == "Gateway 10.9.0.1", "A Location without a customer shows the first line of its notes.");

        list.SelectedItem = rows[1];
        list.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(list) ?? new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters()), 0,
            System.Windows.Input.Key.Enter) { RoutedEvent = UIElement.PreviewKeyDownEvent });
        Require(workspace.Location.Info?.Id == branch!.Id, "Enter switches to the selected Location.");
        Require(Name(list.Items.Cast<object>().Single(row => Name(row).Contains("Branch"))) ==
            string.Format((string)Application.Current.FindResource("Str_Loc_Current"), "Branch office"), "The current mark follows the switch.");

        Require(window.FindName("ProfilesList") == null && window.FindName("SaveProfileButton") == null,
            "Saved scans are Locations now: the separate saved-scans section is gone.");
        Console.WriteLine("PASS: the Locations sidebar lists, marks and opens Locations.");
    }

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, Instance)!.Invoke(target, args);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
