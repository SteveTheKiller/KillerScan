using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerScan.Controls
{
    /// <summary>
    /// A topology box. Device boxes take keyboard focus and appear to screen readers as list
    /// items named after their device, with the same selection as the device table.
    /// </summary>
    /// <summary>
    /// A topology card in the family's active-tab shape (Controls/TabSurfaceBorder.cs): TabRadius
    /// top corners and a TabStripeThickness ring in the device type's colour, full across the top
    /// and fading down the sides, like the active tab's TabActiveRingFadeBrush. Selected cards
    /// wear the accent instead. The ring is rebuilt when the theme changes, since the colours do.
    /// </summary>
    internal sealed class TopologyNode : TabSurfaceBorder
    {
        internal ScanWorkspace Owner { get; }
        internal string RingKey { get; set; } = "TopologyNodeBorderBrush";
        private bool _selected;

        internal TopologyNode(ScanWorkspace owner)
        {
            Owner = owner;
            CornerRadius = new CornerRadius(3, 3, 0, 0);
            Loaded += (_, _) => { Services.ThemeManager.ThemeChanged += RefreshRing; RefreshRing(); };
            Unloaded += (_, _) => Services.ThemeManager.ThemeChanged -= RefreshRing;
        }

        internal bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; RefreshRing(); }
        }

        internal void RefreshRing()
        {
            var stripe = TryFindResource("TabStripeThickness") is Thickness t ? t : new Thickness(1, 3, 1, 0);
            bool resized = stripe != BorderThickness && !double.IsNaN(Width);
            BorderThickness = stripe;
            BorderBrush = Fade(TryFindResource(_selected ? "PrimaryBrush" : RingKey) as Brush);
            // The card's size is pinned to its text (Topology.cs). A theme with another stripe
            // width changes that, so it is measured again around the same center, which is where
            // its links end.
            if (resized)
            {
                double centerX = Canvas.GetLeft(this) + Width / 2, centerY = Canvas.GetTop(this) + Height / 2;
                Width = Height = double.NaN;
                Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Width = Math.Ceiling(DesiredSize.Width);
                Height = Math.Ceiling(DesiredSize.Height);
                Canvas.SetLeft(this, centerX - Width / 2);
                Canvas.SetTop(this, centerY - Height / 2);
            }
        }

        /// <summary>ThemeManager's TabActiveRingFadeBrush recipe applied to any colour.</summary>
        private static Brush? Fade(Brush? brush)
        {
            if (brush is not SolidColorBrush solid) return brush;
            var c = solid.Color;
            var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fade.GradientStops.Add(new GradientStop(c, 0));
            fade.GradientStops.Add(new GradientStop(c, 0.3));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            fade.Freeze();
            return fade;
        }

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
            border.KeyDown += TopologyNode_KeyDown;
            border.GotKeyboardFocus += (_, _) =>
            {
                _topologyFocusIp = device.IpAddress;
                UpdateTopologyTabStop();
            };
        }

        /// <summary>
        /// The diagram is one Tab stop, like a list: only the device last focused (or the first
        /// one) takes Tab, the arrow keys move between devices, and the next Tab leaves the
        /// diagram instead of visiting every device on the way out.
        /// </summary>
        private void UpdateTopologyTabStop()
        {
            var nodes = TopologyCanvas.Children.OfType<Border>().Where(b => b.Tag is NetworkDevice).ToList();
            var current = nodes.FirstOrDefault(b => ((NetworkDevice)b.Tag).IpAddress == _topologyFocusIp) ?? nodes.FirstOrDefault();
            foreach (var node in nodes) KeyboardNavigation.SetIsTabStop(node, node == current);
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
            Point Center(Border b) => new(Canvas.GetLeft(b) + b.Width / 2, Canvas.GetTop(b) + b.Height / 2);
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
            UpdateTopologyTabStop();
            if (!hadFocus || _topologyFocusIp == null) return;
            TopologyCanvas.Children.OfType<Border>()
                .FirstOrDefault(b => b.Tag is NetworkDevice d && d.IpAddress == _topologyFocusIp)?.Focus();
        }
    }
}
