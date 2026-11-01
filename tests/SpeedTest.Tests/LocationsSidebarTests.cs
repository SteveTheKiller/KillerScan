using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Services;
using KillerScan.Shell;

// Manage Locations shows every Location in a flat sidebar list, marks the current one with a check,
// and opens one with Enter (a click only selects). Runs inside the history suite's host, which points Locations at a
// scratch folder before the window loads them.
internal static class LocationsSidebarTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    internal static void Run()
    {
        CheckDemoLocations();
        DemoData.Enabled = false;
        var window = new MainWindow();
        Require(LocationService.Loaded && LocationService.Editable && LocationService.Items.Count == 0,
            "The window loads an empty scratch catalog, never the real one.");
        LocationService.Create("Acme Dental", out var acme);
        if (acme == null) throw new InvalidOperationException("The fixture Location was not created.");
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
        bool Current(object row) => (bool)row.GetType().GetProperty("IsCurrent")!.GetValue(row)!;
        string Detail(object row) => (string)row.GetType().GetProperty("Detail")!.GetValue(row)!;
        Require(rows.Count == 2 && Name(rows[0]) == "Acme Dental" && Current(rows[0]) && !Current(rows[1]),
            "Every Location is listed and the current one is marked.");
        Require(Detail(rows[1]) == "Gateway 10.9.0.1", "A Location without a customer shows the first line of its notes.");

        Require(rows.All(row => row.GetType().GetProperty("Children") == null),
            "The list is flat: saved scans live in the Location picker, not under each Location.");
        list.SelectedItem = rows[1];
        Require(workspace.Location.Info?.Id == acme.Id, "Selecting a Location does not switch to it.");
        list.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(list) ?? new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters()), 0,
            System.Windows.Input.Key.Enter) { RoutedEvent = UIElement.PreviewKeyDownEvent });
        Require(workspace.Location.Info?.Id == branch!.Id, "Enter switches to the selected Location.");
        Require(Current(list.Items.Cast<object>().Single(row => Name(row) == "Branch office")), "The current mark follows the switch.");

        Require(window.FindName("ProfilesList") == null && window.FindName("SaveProfileButton") == null,
            "Saved scans are Locations now: the separate saved-scans section is gone.");
        Console.WriteLine("PASS: the Locations sidebar lists, marks and opens Locations.");
    }

    private static void CheckDemoLocations()
    {
        DemoData.Enabled = true;
        var window = new MainWindow();
        try
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1200, 800));
            content.Arrange(new Rect(0, 0, 1200, 800));
            content.UpdateLayout();
            ((Button)window.FindName("LocationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var list = (ListBox)window.FindName("LocationsList");
            Require(list.Items.Count == 3, "Demo Locations shows three sample sites instead of an empty panel.");
            var workspace = FindWorkspace(content) ?? throw new InvalidOperationException("The demo window contains its scan workspace.");
            var picker = ((Button)workspace.FindName("LocationButton")).ContextMenu;
            var sites = picker.Items.OfType<MenuItem>().Where(item => item.IsCheckable).ToList();
            Require(sites.Count == 3 && sites.Count(item => item.IsChecked) == 1,
                "The demo picker shows Locations and marks the current site, not just legacy saved scans.");
            Require(picker.Items.OfType<MenuItem>().Any(item => Equals(item.Header,
                Application.Current.FindResource("Str_Loc_Manage")) && item.IsEnabled),
                "The demo Location picker includes Manage Locations.");
            sites.Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(((TextBlock)workspace.FindName("LocationLabel")).Text == sites.Last().Header.ToString(),
                "Picking a demo Location changes the current site.");
            Require(!System.IO.Directory.Exists(LocationService.Root), "Demo Locations creates no profile directory.");
        }
        finally { window.Close(); }
    }

    private static KillerScan.Controls.ScanWorkspace? FindWorkspace(DependencyObject root)
    {
        if (root is KillerScan.Controls.ScanWorkspace workspace) return workspace;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindWorkspace(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, Instance)!.Invoke(target, args);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
