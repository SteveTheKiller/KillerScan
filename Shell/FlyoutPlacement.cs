using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace KillerScan.Shell
{
    /// <summary>Shared popup placement for pane flyouts and button-anchored rail menus.</summary>
    internal static class FlyoutPlacement
    {
        private static FrameworkElement? _pane;
        private static FrameworkElement? _root;
        private static readonly Thickness DefaultCardHalo = new(22, 18, 22, 26);

        internal static void UsePane(FrameworkElement pane, FrameworkElement root)
        {
            _pane = pane;
            _root = root;
        }

        internal static void Attach(Popup popup)
        {
            popup.PlacementTarget = _root;
            popup.Placement = PlacementMode.Custom;
            popup.HorizontalOffset = 0;
            popup.VerticalOffset = 0;
            Thickness halo = popup.Child is FrameworkElement card ? card.Margin : DefaultCardHalo;
            popup.CustomPopupPlacementCallback =
                (popupSize, _, _) => Place(popupSize, halo);
        }

        internal static void Attach(ContextMenu menu)
        {
            menu.PlacementTarget = _root;
            menu.Placement = PlacementMode.Custom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.ApplyTemplate();
            Thickness halo = menu.Template?.FindName("MenuRoot", menu) is FrameworkElement card
                ? card.Margin : DefaultCardHalo;
            menu.CustomPopupPlacementCallback =
                (popupSize, _, _) => Place(popupSize, halo);
        }

        internal static void Attach(ContextMenu menu, FrameworkElement button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Custom;
            menu.HorizontalOffset = menu.VerticalOffset = 0;
            menu.ApplyTemplate();
            Thickness halo = menu.Template?.FindName("MenuRoot", menu) is FrameworkElement card
                ? card.Margin : DefaultCardHalo;
            menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                var dpi = VisualTreeHelper.GetDpi(button);
                double sx = menu.ActualWidth > 0 ? popupSize.Width / menu.ActualWidth : dpi.DpiScaleX;
                double sy = menu.ActualHeight > 0 ? popupSize.Height / menu.ActualHeight : dpi.DpiScaleY;
                return PlaceBesideButton(popupSize, targetSize, halo, sx, sy, dpi.DpiScaleX);
            };
        }

        internal static CustomPopupPlacement[] PlaceBesideButton(Size popupSize, Size targetSize,
            Thickness halo, double popupScaleX, double popupScaleY, double dpiScaleX)
        {
            // Callback sizes are screen pixels. Remove the transparent shadow halo so the
            // visible card has an eight-DIP gap, independent of app zoom. WPF fits the edge.
            double x = targetSize.Width + 8 * dpiScaleX - halo.Left * popupScaleX;
            return [
                new CustomPopupPlacement(new Point(x, -halo.Top * popupScaleY), PopupPrimaryAxis.Vertical),
                new CustomPopupPlacement(new Point(x, targetSize.Height - popupSize.Height + halo.Bottom * popupScaleY), PopupPrimaryAxis.Vertical)
            ];
        }

        private static CustomPopupPlacement[] Place(Size popupSize, Thickness halo)
        {
            if (_pane == null || _root == null)
                return [new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None)];

            // Place against an UNSCALED root. Using the zoomed pane itself as PlacementTarget
            // made WPF multiply the theme popup's coordinates by AppScale, while ContextMenu
            // followed a different HWND path. This gives both menu types identical coordinates.
            Point corner = _pane.TransformToAncestor(_root).Transform(new Point(0, _pane.ActualHeight));
            // The measured popup includes the visible card's transparent shadow halo.
            // Place the visible card eight pixels inside the pane, beside the rail and above
            // the content edge. Custom placement receives offsets as an argument; it does not
            // apply them after this callback. Item padding remains inside the visible card.
            const double visibleCardInset = 8;
            double x = corner.X + visibleCardInset - halo.Left;
            double y = corner.Y - popupSize.Height + halo.Bottom - visibleCardInset;
            if (y < 0) y = 0;
            return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
        }
    }
}
