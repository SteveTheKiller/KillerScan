using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using KillerScan.Controls;
using KillerScan.Engine;

// Every topology device node can be reached, selected, announced and moved from the keyboard,
// and keeps keyboard focus when the diagram is rebuilt.
internal static class TopologyKeyboardTests
{
    internal static void Run()
    {
        using var workspace = new ScanWorkspace("192.0.2.0/24", false);
        var window = new Window
        {
            Content = workspace, Width = 1200, Height = 800, Left = -20000, Top = -20000,
            ShowInTaskbar = false, ShowActivated = true, WindowStyle = WindowStyle.None
        };
        window.Show();
        try
        {
            workspace.LoadSnapshot("192.0.2.0/24", Enumerable.Range(1, 6).Select(i => new NetworkDevice
                { IpAddress = "192.0.2." + (i * 10), Hostname = "node-" + i, DeviceType = "Workstation" }));
            workspace.SetView("topology");
            Pump();
            var canvas = (Canvas)workspace.FindName("TopologyCanvas");
            var grid = (DataGrid)workspace.FindName("ResultsGrid");
            var nodes = canvas.Children.OfType<Border>().Where(b => b.Tag is NetworkDevice).ToList();
            Require(nodes.Count == 6 && nodes.All(n => n.Focusable), "Every device node can take keyboard focus.");

            var peer = UIElementAutomationPeer.CreatePeerForElement(nodes[0]);
            Require(peer != null && peer.GetAutomationControlType() == AutomationControlType.ListItem &&
                peer.GetName().Contains(((NetworkDevice)nodes[0].Tag).IpAddress) && peer.IsKeyboardFocusable(),
                "Screen readers see each node as a focusable list item named with its device.");
            var selection = (ISelectionItemProvider)peer!.GetPattern(PatternInterface.SelectionItem)!;
            selection.Select();
            Require(selection.IsSelected && grid.SelectedItems.Count == 1 && grid.SelectedItems[0] == nodes[0].Tag,
                "Selecting a node through accessibility selects its device.");

            Require(nodes[0].Focus() && nodes[0].IsKeyboardFocused, "A node takes keyboard focus.");
            Require(nodes.Count(KeyboardNavigation.GetIsTabStop) == 1 && KeyboardNavigation.GetIsTabStop(nodes[0]),
                "The diagram is one Tab stop: only the focused device takes Tab.");
            nodes[0].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Require(Keyboard.FocusedElement is not Border { Tag: NetworkDevice }, "Tab from a device leaves the diagram rather than visiting every device.");
            var start = nodes[0];
            start.Focus();
            foreach (var key in new[] { Key.Right, Key.Down, Key.Left, Key.Up })
            {
                var before = Keyboard.FocusedElement;
                Press(start, key);
                if (Keyboard.FocusedElement != before) break;
            }
            var moved = Keyboard.FocusedElement as Border;
            Require(moved != null && moved != start && moved.Tag is NetworkDevice, "Arrow keys move focus to a neighboring node.");
            Require(grid.SelectedItems.Count == 1 && grid.SelectedItems[0] == moved!.Tag,
                "An arrow key also selects the device it moves to, so device actions apply to the focused one.");
            foreach (var key in new[] { Key.Right, Key.Down, Key.Left, Key.Up })
            {
                var before = Keyboard.FocusedElement;
                Press(moved!, key, ModifierKeys.Control);
                if (Keyboard.FocusedElement != before) break;
            }
            Require(Keyboard.FocusedElement != moved && grid.SelectedItems.Count == 1 && grid.SelectedItems[0] == moved!.Tag,
                "Ctrl+arrow moves focus without changing the selection.");

            Press(moved!, Key.Space);
            Require(grid.SelectedItems.Count == 1 && grid.SelectedItems[0] == moved!.Tag, "Space selects the focused node alone.");
            Press(start, Key.Space, ModifierKeys.Control);
            Require(grid.SelectedItems.Count == 2, "Ctrl+Space adds a node to the selection.");
            Press(start, Key.Space, ModifierKeys.Control);
            Require(grid.SelectedItems.Count == 1, "Ctrl+Space toggles it off again.");

            moved!.Focus();
            double left = Canvas.GetLeft(moved), top = Canvas.GetTop(moved);
            Press(moved, Key.Right, ModifierKeys.Shift);
            Press(moved, Key.Down, ModifierKeys.Shift);
            Require(Canvas.GetLeft(moved) > left && Canvas.GetTop(moved) > top, "Shift+arrow keys move the node.");

            var device = (NetworkDevice)moved.Tag;
            workspace.Session.Devices.Add(new NetworkDevice { IpAddress = "192.0.2.200", Hostname = "late" });
            Pump(800);
            var after = canvas.Children.OfType<Border>().Single(b => b.Tag == device);
            Require(after.IsKeyboardFocused, "Keyboard focus stays on the same device after the diagram is rebuilt.");
            Require(Math.Abs(Canvas.GetLeft(after) - Canvas.GetLeft(moved)) < 1 && Math.Abs(Canvas.GetTop(after) - Canvas.GetTop(moved)) < 1,
                "A node moved from the keyboard keeps its place after a rebuild.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS: topology nodes are focusable, announced, selectable and movable from the keyboard.");
    }

    private static void Press(UIElement target, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        var source = PresentationSource.FromVisual(target)!;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.KeyDownEvent };
        TestModifiers = modifiers;
        try { target.RaiseEvent(args); }
        finally { TestModifiers = null; }
    }

    // Keyboard.Modifiers reads the real keyboard, so the workspace consults this override in tests.
    private static ModifierKeys? TestModifiers
    {
        set => typeof(ScanWorkspace).GetProperty("KeyboardModifiersOverride",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, value);
    }

    private static void Pump(int milliseconds = 200)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
