using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private double _topologyZoom = 1;
        private MenuItem? _topologyZoomMenu;

        private void AddTopologyZoomMenu(ContextMenu menu)
        {
            _topologyZoomMenu = new MenuItem { Header = "100%", InputGestureText = "Ctrl+Wheel", Icon = MenuGlyph.Create(0xE71E) };
            foreach (double zoom in new[] { 0.25, 0.5, 0.75, 1, 1.5, 2, 3 })
            {
                var item = new MenuItem { Header = zoom.ToString("P0"), Tag = zoom, IsCheckable = true, Icon = MenuGlyph.Create(0xE71E) };
                item.Click += (_, _) => SetTopologyZoom(zoom,
                    new Point(TopologyScrollViewer.ViewportWidth / 2, TopologyScrollViewer.ViewportHeight / 2));
                _topologyZoomMenu.Items.Add(item);
            }
            menu.Items.Add(_topologyZoomMenu);
            UpdateTopologyZoomMenu();
        }

        private void UpdateTopologyZoomMenu()
        {
            if (_topologyZoomMenu == null) return;
            _topologyZoomMenu.Header = _topologyZoom.ToString("P0");
            foreach (MenuItem item in _topologyZoomMenu.Items)
                item.IsChecked = Math.Abs((double)item.Tag - _topologyZoom) < 0.0001;
        }

        private void TopologyScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var presenter = TopologyScrollViewer.Template?.FindName("PART_ScrollContentPresenter", TopologyScrollViewer) as UIElement;
            e.Handled = HandleTopologyWheel(e.Delta, Keyboard.Modifiers, e.GetPosition(presenter ?? TopologyScrollViewer));
        }

        private bool HandleTopologyWheel(int delta, ModifierKeys modifiers, Point pointer)
        {
            if ((modifiers & ModifierKeys.Control) == 0) return false;
            SetTopologyZoom(TopologyGeometry.WheelZoom(_topologyZoom, delta), pointer);
            return true;
        }

        private void SetTopologyZoom(double zoom, Point pointer)
        {
            zoom = Math.Max(TopologyGeometry.MinimumZoom, Math.Min(TopologyGeometry.MaximumZoom, zoom));
            if (Math.Abs(zoom - _topologyZoom) < 0.000001) return;
            double x = TopologyGeometry.AnchoredOffset(TopologyScrollViewer.HorizontalOffset, pointer.X, _topologyZoom, zoom);
            double y = TopologyGeometry.AnchoredOffset(TopologyScrollViewer.VerticalOffset, pointer.Y, _topologyZoom, zoom);
            _topologyZoom = zoom;
            TopologyZoomHost.LayoutTransform = new ScaleTransform(zoom, zoom);
            TopologyScrollViewer.UpdateLayout();
            // WPF clamps offsets at the canvas edges, where exact anchoring is impossible.
            TopologyScrollViewer.ScrollToHorizontalOffset(x);
            TopologyScrollViewer.ScrollToVerticalOffset(y);
            TopologyScrollViewer.UpdateLayout();
            UpdateTopologyZoomMenu();
        }
    }
}
