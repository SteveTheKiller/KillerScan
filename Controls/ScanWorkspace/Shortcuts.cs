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
            bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
            bool shift = modifiers.HasFlag(ModifierKeys.Shift);
            bool alt = modifiers.HasFlag(ModifierKeys.Alt);
            bool text = Keyboard.FocusedElement is TextBox;
            Action? action = null;
            if (e.Key == Key.Escape && FilterBox.Visibility == Visibility.Visible) action = CloseFilter;
            else if (e.Key == Key.Escape && IsScanning) action = Stop;
            else if (ctrl && shift && !alt && e.Key == Key.F) action = FocusFilter;
            else if (ctrl && !shift && !alt && e.Key == Key.F) action = FocusTargets;
            else if (ctrl && !shift && !alt && e.Key == Key.E) action = () => ExportCsv_Click(this, new RoutedEventArgs());
            else if (ctrl && !shift && !alt && e.Key == Key.R) action = RescanSelected;
            else if (ctrl && !shift && !alt && e.Key >= Key.D1 && e.Key <= Key.D4) action = () => ShowTopologyOrder(e.Key - Key.D1);
            else if (ctrl && !shift && !alt && e.Key >= Key.NumPad1 && e.Key <= Key.NumPad4) action = () => ShowTopologyOrder(e.Key - Key.NumPad1);
            else if (ctrl && !text)
            {
                if (e.Key == Key.A && !shift && !alt) action = SelectAll;
                else if (e.Key == Key.C) action = () =>
                {
                    if (shift) CopyMac_Click(this, new RoutedEventArgs());
                    else if (alt) CopyHostname_Click(this, new RoutedEventArgs());
                    else CopyIp_Click(this, new RoutedEventArgs());
                };
                else if (!alt && e.Key == Key.S) action = () => RaiseDeviceAction(shift ? "SshAs" : "Ssh");
                else if (!alt && !shift && e.Key == Key.P) action = () => RaiseDeviceAction("Ping");
                else if (!alt && !shift && e.Key == Key.D) action = () => RaiseDeviceAction("Rdp");
            }
            else if (modifiers == ModifierKeys.None)
            {
                if (e.Key == Key.F5) action = Scan;
                else if (e.Key == Key.F7) action = CycleTopologyOrder;
                else if (e.Key == Key.F8) action = () => SetView(_showServices ? "devices" : "services");
                else if (e.Key == Key.F9) action = () => SetView(_showTopology ? "devices" : "topology");
                else if (e.Key == Key.Enter && !text && SelectedDevice != null) action = () => RaiseDeviceAction("Browser");
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
