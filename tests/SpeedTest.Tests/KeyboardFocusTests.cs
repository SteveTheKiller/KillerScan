using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KillerScan.Controls;
using KillerScan.Shell;
using KillerScan.Engine;

internal static class KeyboardFocusTests
{
    private static int _checks;
    internal static void Run()
    {
        KillerFamily.Accessibility.AccessibleControls.Initialize();
        var bare = new Button { Content = "Settings", Style = (Style)Application.Current.FindResource("BareButton") };
        var custom = new Style(typeof(Control));
        var deliberate = new Button { Content = "Custom", FocusVisualStyle = custom };
        var link = new Hyperlink(new Run("Steve the Killer"));
        var panel = new StackPanel();
        panel.Children.Add(bare);
        panel.Children.Add(deliberate);
        panel.Children.Add(new TextBlock(link));
        var window = new Window
        {
            Content = panel, Width = 500, Height = 300, Left = -20000, Top = -20000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
        };
        window.Show();
        try
        {
            Pump();
            Require(HasFamilyRing(bare.FocusVisualStyle), "A normal BareButton uses the themed family ring instead of the WPF default black cue.");
            Require(ReferenceEquals(deliberate.FocusVisualStyle, custom), "A deliberate custom focus visual is preserved.");
            link.Focus();
            Pump();
            Require(HasFamilyRing(link.FocusVisualStyle), "A visible hyperlink receives the family keyboard focus ring.");
            var tab = new ViewTab(() => { }, () => false) { Focusable = true };
            Require(HasFamilyRing(tab.FocusVisualStyle), "An inactive view tab has the family focus ring independently of selection.");
            using var workspace = new ScanWorkspace("192.0.2.0/24", false);
            var first = new NetworkDevice { IpAddress = "192.0.2.10" };
            var second = new NetworkDevice { IpAddress = "192.0.2.11" };
            workspace.LoadSnapshot("192.0.2.0/24", [first, second]);
            window.Content = workspace;
            Pump();
            var grid = (DataGrid)workspace.FindName("ResultsGrid");
            Require(grid.Focus(), "The device table accepts keyboard entry.");
            Pump();
            Require(Keyboard.FocusedElement is DataGridCell, "Entering the populated device table focuses a native cell instead of an empty container stop.");
            var cell = (DataGridCell)Keyboard.FocusedElement;
            var peer = UIElementAutomationPeer.CreatePeerForElement(cell)!;
            Require(peer.GetName() == "192.0.2.10", "The focused first cell announces its IP address through UI Automation.");
            Require(grid.SelectedItems.Contains(first), "The entry row is selected so table entry has visible feedback.");
            cell.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(cell), 0, Key.Down)
            { RoutedEvent = Keyboard.KeyDownEvent });
            Pump();
            Require(ReferenceEquals(grid.CurrentItem, second), "The standard DataGrid Down key moves to the next row.");
            var gridPeer = UIElementAutomationPeer.CreatePeerForElement(grid)!;
            Require(gridPeer.GetName() == (string)Application.Current.FindResource("Str_View_Devices") &&
                !string.IsNullOrWhiteSpace(gridPeer.GetHelpText()), "The table has a localized UI Automation name and keyboard help.");
            cell = (DataGridCell)Keyboard.FocusedElement;
            cell.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(cell), 0, Key.Tab)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump();
            Require(!grid.IsKeyboardFocusWithin, "Tab leaves the table in one step.");
            grid.Focus();
            Pump();
            Require(ReferenceEquals(grid.CurrentItem, second), "Reentering the table keeps the current device.");
            var services = (DataGrid)workspace.FindName("ServicesGrid");
            var servicesPeer = UIElementAutomationPeer.CreatePeerForElement(services)!;
            Require(servicesPeer.GetName() == (string)Application.Current.FindResource("Str_Services_Title") &&
                !string.IsNullOrWhiteSpace(servicesPeer.GetHelpText()), "The Services table also has a localized UI Automation name and keyboard help.");
            workspace.LoadSnapshot("192.0.2.0/24", []);
            grid.Focus();
            Pump();
            Require(grid.IsKeyboardFocused && HasFamilyRing(grid.FocusVisualStyle), "An empty table keeps its named container focus and family cue.");
        }
        finally { window.Close(); }
        Console.WriteLine($"PASS: button, hyperlink and tab focus styles, table entry and native navigation ({_checks} checks).");
    }

    private static bool HasFamilyRing(Style? style)
    {
        if (style == null) return false;
        var visual = new Control { Style = style };
        visual.ApplyTemplate();
        return VisualTreeHelper.GetChildrenCount(visual) > 0 &&
            VisualTreeHelper.GetChild(visual, 0).GetType().FullName == "KillerFamily.Accessibility.FocusRing";
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
