using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerScan.Controls
{
    /// <summary>
    /// A topology box. Device boxes take keyboard focus and appear to screen readers as list
    /// items named after their device, with the same selection as the device table.
    /// </summary>
    internal sealed class TopologyNode(ScanWorkspace owner) : Border
    {
        internal ScanWorkspace Owner { get; } = owner;
        protected override AutomationPeer OnCreateAutomationPeer() => new TopologyNodePeer(this);
    }

    internal sealed class TopologyNodePeer(TopologyNode node) : FrameworkElementAutomationPeer(node), ISelectionItemProvider
    {
        private NetworkDevice? Device => node.Tag as NetworkDevice;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetClassNameCore() => "TopologyNode";
        protected override bool IsKeyboardFocusableCore() => Device != null;
        protected override string GetNameCore() => AutomationProperties.GetName(node);
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.SelectionItem && Device != null ? this : base.GetPattern(patternInterface);

        public bool IsSelected => Device != null && node.Owner.IsTopologySelected(Device);
        public IRawElementProviderSimple? SelectionContainer => null;
        public void Select() { if (Device != null) node.Owner.SelectTopologyDevice(Device, only: true); }
        public void AddToSelection() { if (Device != null && !IsSelected) node.Owner.SelectTopologyDevice(Device, only: false); }
        public void RemoveFromSelection() { if (Device != null && IsSelected) node.Owner.SelectTopologyDevice(Device, only: false); }
    }

    public partial class ScanWorkspace
    {
        /// <summary>Lets tests press modifier keys without a physical keyboard.</summary>
        internal static ModifierKeys? KeyboardModifiersOverride { get; set; }
        private static ModifierKeys CurrentModifiers => KeyboardModifiersOverride ?? Keyboard.Modifiers;

        /// <summary>The device whose node had keyboard focus, so a rebuild can give it back.</summary>
        private string? _topologyFocusIp;

        internal bool IsTopologySelected(NetworkDevice device) => ResultsGrid.SelectedItems.Contains(device);

        /// <summary>Selects one device alone, or toggles it in the selection, as Space and Ctrl+Space do.</summary>
        internal void SelectTopologyDevice(NetworkDevice device, bool only)
        {
            if (only)
            {
                ResultsGrid.SelectedItems.Clear();
                ResultsGrid.SelectedItems.Add(device);
            }
            else if (ResultsGrid.SelectedItems.Contains(device)) ResultsGrid.SelectedItems.Remove(device);
            else ResultsGrid.SelectedItems.Add(device);
            ResultsGrid.CurrentItem = device;
            UpdateTopologySelectionVisuals();
        }

        private void MakeTopologyNodeKeyboardReady(Border border, string title, string detail)
        {
            AutomationProperties.SetName(border, string.IsNullOrWhiteSpace(detail) ? title : title + ", " + detail);
            if (border.Tag is not NetworkDevice device) return;
            border.Focusable = true;
            KeyboardNavigation.SetIsTabStop(border, true);
            border.KeyDown += TopologyNode_KeyDown;
            border.GotKeyboardFocus += (_, _) => _topologyFocusIp = device.IpAddress;
        }

        private void TopologyNode_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not Border { Tag: NetworkDevice device } border) return;
            var modifiers = CurrentModifiers;
            Vector direction = e.Key switch
            {
                Key.Left => new Vector(-1, 0), Key.Right => new Vector(1, 0),
                Key.Up => new Vector(0, -1), Key.Down => new Vector(0, 1),
                _ => default
            };
            if (e.Key == Key.Space && (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Control))
            {
                SelectTopologyDevice(device, only: modifiers == ModifierKeys.None);
                e.Handled = true;
            }
            else if (direction != default && modifiers == ModifierKeys.Shift)
            {
                // The keyboard version of dragging: moves this node and remembers where it is.
                _topologyDragStarts.Clear();
                _topologyDragStarts[border] = new Point(Canvas.GetLeft(border), Canvas.GetTop(border));
                MoveTopologyNodes(direction * 10 * TopologyScale);
                _topologyDragStarts.Clear();
                e.Handled = true;
            }
            else if (direction != default && modifiers is ModifierKeys.None or ModifierKeys.Control)
            {
                // As in a list: an arrow moves and selects, so Enter, F2 and the other device
                // actions apply to the device that has focus. Ctrl+arrow moves without changing
                // the selection, for building one with Ctrl+Space.
                if (NearestTopologyNode(border, direction) is { Tag: NetworkDevice nextDevice } next)
                {
                    next.Focus();
                    if (modifiers == ModifierKeys.None) SelectTopologyDevice(nextDevice, only: true);
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// The device node closest in a direction: it must lie mostly that way, and sideways
        /// distance counts double so focus moves along a row or column before jumping across.
        /// </summary>
        private Border? NearestTopologyNode(Border from, Vector direction)
        {
            Point Center(Border b) => new(Canvas.GetLeft(b) + TopologyNodeWidth / 2, Canvas.GetTop(b) + TopologyNodeHeight / 2);
            var origin = Center(from);
            return TopologyCanvas.Children.OfType<Border>()
                .Where(b => b != from && b.Tag is NetworkDevice)
                .Select(b => (Node: b, Offset: Center(b) - origin))
                .Select(c => (c.Node, Along: c.Offset.X * direction.X + c.Offset.Y * direction.Y,
                    Across: Math.Abs(c.Offset.X * direction.Y - c.Offset.Y * direction.X)))
                .Where(c => c.Along > 0 && c.Along >= c.Across)
                .OrderBy(c => c.Along + 2 * c.Across)
                .Select(c => c.Node)
                .FirstOrDefault();
        }

        /// <summary>After a rebuild, gives keyboard focus back to the node that had it.</summary>
        private void RestoreTopologyFocus(bool hadFocus)
        {
            if (!hadFocus || _topologyFocusIp == null) return;
            TopologyCanvas.Children.OfType<Border>()
                .FirstOrDefault(b => b.Tag is NetworkDevice d && d.IpAddress == _topologyFocusIp)?.Focus();
        }
    }
}
