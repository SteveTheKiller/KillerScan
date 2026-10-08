using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        public void RescanSelected() => RescanSelected_Click(this, new RoutedEventArgs());
        public void SelectAll()
        {
            if (_showServices) ServicesGrid.SelectAll();
            else ResultsGrid.SelectAll();
            if (_showTopology) UpdateTopologySelectionVisuals();
        }
        public void CycleTopologyOrder()
        {
            SetView("topology");
            SetTopologyOrder((TopologyOrder)(((int)_topologyOrder + 1) % 4));
        }
        public void ShowTopologyOrder(int order)
        {
            if (order < 0 || order > 3) return;
            SetView("topology");
            SetTopologyOrder((TopologyOrder)order);
        }
        public void HandleKey(KeyEventArgs e)
        {
            if (e.Handled) return;
            var modifiers = Keyboard.Modifiers;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
            bool shift = modifiers.HasFlag(ModifierKeys.Shift);
            bool alt = modifiers.HasFlag(ModifierKeys.Alt);
            bool text = Keyboard.FocusedElement is TextBox;
            Action? action = null;
            if (!text && _showServices && HandleServiceShortcut(key, modifiers)) { e.Handled = true; return; }
            if (!text && ((key == Key.Apps && modifiers == ModifierKeys.None) || (key == Key.F10 && modifiers == ModifierKeys.Shift)))
            {
                if (_showTopology && SelectedDevice == null)
                {
                    UpdateTopologyOrderUi();
                    TopologyOrderButton.ContextMenu.PlacementTarget = TopologyCanvas;
                    TopologyOrderButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    TopologyOrderButton.ContextMenu.IsOpen = true; e.Handled = true; return;
                }
                PrepareDeviceContextMenu();
                UIElement? target = _showTopology
                    ? TopologyCanvas.Children.OfType<Border>().FirstOrDefault(border => ReferenceEquals(border.Tag, SelectedDevice))
                    : (ResultsGrid.SelectedItem == null ? null : ResultsGrid.ItemContainerGenerator.ContainerFromItem(ResultsGrid.SelectedItem)) as UIElement;
                ResultsGrid.ContextMenu.PlacementTarget = target ?? (_showTopology ? (UIElement)TopologyCanvas : ResultsGrid);
                ResultsGrid.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                ResultsGrid.ContextMenu.IsOpen = true; e.Handled = true; return;
            }
            if (!text && modifiers == ModifierKeys.None && key == Key.F2) action = () => RenameDevice_Click(this, new RoutedEventArgs());
            else if (!text && modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.B) action = () => TrustDevice_Click(this, new RoutedEventArgs());
            else if (!text && modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.N) action = () => ClearDeviceName_Click(this, new RoutedEventArgs());
            else if (!text && modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.T) action = () => ClearOverride_Click(this, new RoutedEventArgs());
            else if (key == Key.Escape && FilterBox.Visibility == Visibility.Visible) action = CloseFilter;
            else if (key == Key.Escape && IsScanning) action = Stop;
            else if (ctrl && shift && !alt && key == Key.F) action = FocusFilter;
            else if (ctrl && !shift && !alt && key == Key.F) action = FocusTargets;
            else if (ctrl && !shift && !alt && key == Key.E) action = () =>
            {
                if (_showServices) ExportServicesCsv_Click(this, new RoutedEventArgs());
                else ExportCsv_Click(this, new RoutedEventArgs());
            };
            else if (ctrl && !shift && !alt && key == Key.R) action = RescanSelected;
            // Ctrl+G, not an F key: arranging the topology is a thing you do repeatedly while
            // looking at it, so it sits with the other Ctrl chords rather than up on the F row.
            else if (ctrl && !shift && !alt && key == Key.G) action = CycleTopologyOrder;
            else if (ctrl && !shift && !alt && key >= Key.D1 && key <= Key.D4) action = () => ShowTopologyOrder(key - Key.D1);
            else if (ctrl && !shift && !alt && key >= Key.NumPad1 && key <= Key.NumPad4) action = () => ShowTopologyOrder(key - Key.NumPad1);
            else if (ctrl && !text)
            {
                if (key == Key.A && !shift && !alt) action = SelectAll;
                else if (key == Key.C) action = () =>
                {
                    if (shift) CopyMac_Click(this, new RoutedEventArgs());
                    else if (alt) CopyHostname_Click(this, new RoutedEventArgs());
                    else CopyIp_Click(this, new RoutedEventArgs());
                };
                else if (!alt && key == Key.S) action = () => RaiseDeviceAction(shift ? "SshAs" : "Ssh");
                else if (!alt && !shift && key == Key.P) action = () => RaiseDeviceAction("Ping");
                else if (!alt && !shift && key == Key.D) action = () => RaiseDeviceAction("Rdp");
            }
            else if (modifiers == ModifierKeys.None)
            {
                if (key == Key.F5) action = Scan;
                else if (key == Key.F7) action = () => SetView("services");
                else if (key == Key.F8) action = () => SetView("topology");
                else if (key == Key.Enter && !text && SelectedDevice != null) action = () => RaiseDeviceAction("Browser");
            }
            if (action != null) { action(); e.Handled = true; }
        }
        private void Menu_ForwardWheelToGrid(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            var point = Mouse.GetPosition(ResultsGrid);
            if (point.X < 0 || point.Y < 0 || point.X > ResultsGrid.ActualWidth || point.Y > ResultsGrid.ActualHeight) return;
            var viewer = FindChildScrollViewer(ResultsGrid);
            if (viewer == null) return;
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset +
                (viewer.CanContentScroll ? (e.Delta > 0 ? -3 : 3) : -e.Delta));
            e.Handled = true;
        }
        private static ScrollViewer? FindChildScrollViewer(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer scroll) return scroll;
                var found = FindChildScrollViewer(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}
