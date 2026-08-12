using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerScan.Shell
{
    /// <summary>Anchors every rail flyout to the content pane's lower-left corner.</summary>
    internal static class FlyoutPlacement
    {
        private static FrameworkElement? _pane;
        private static FrameworkElement? _root;

        internal static void UsePane(FrameworkElement pane, FrameworkElement root)
        {
            _pane = pane;
            _root = root;
        }

        internal static void Attach(Popup popup)
        {
            popup.PlacementTarget = _root;
            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback = Place;
        }

        internal static void Attach(ContextMenu menu)
        {
            menu.PlacementTarget = _root;
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = Place;
        }

        private static CustomPopupPlacement[] Place(Size popupSize, Size targetSize, Point _)
        {
            if (_pane == null || _root == null)
                return [new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None)];

            // Place against an UNSCALED root. Using the zoomed pane itself as PlacementTarget
            // made WPF multiply the theme popup's coordinates by AppScale, while ContextMenu
            // followed a different HWND path. This gives both menu types identical coordinates.
            Point corner = _pane.TransformToAncestor(_root).Transform(new Point(0, _pane.ActualHeight));
            // ContextMenu's family-standard template reserves 18px above and 26px below for
            // its shadow, then applies a -12px vertical offset.  The callback is handed those
            // invisible bounds, not the visible card.  Compensate 32px so the card itself ends
            // 6px above the pane/footer corner, matching KillerShell and KillerNotes:
            //     -12 offset - 26 bottom halo + 32 compensation = -6 visible inset.
            const double visibleCardCompensation = 32;
            double y = corner.Y - popupSize.Height + visibleCardCompensation;
            if (y < 0) y = 0;
            return [new CustomPopupPlacement(new Point(corner.X, y), PopupPrimaryAxis.None)];
        }
    }
}
