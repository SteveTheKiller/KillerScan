using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerScan.Shell;

namespace KillerScan.Controls
{
    /// <summary>
    /// Where every flyout opens: in the content pane, against the edge beside the icon rail, at
    /// the height of the rail button that opened it.
    ///
    /// The bottom buttons' menus sit in the pane's corner, eight pixels in from the rail and from
    /// the pane's foot. A menu opened from a button higher up rises with it: its visible bottom
    /// edge lines up with the button's bottom, so a one-item menu sits beside its button instead
    /// of stranded down in the corner (Steve, 2026-10-26). It never drops below the corner and
    /// never rises past the pane's top. With the sidebar on the right the same rule mirrors; on
    /// the top or bottom the menu takes the pane's near corner, as before.
    ///
    /// Why the pane and not the button or a built-in mode: a Popup is its own top-level window and
    /// WPF's modes only avoid the SCREEN edge. They do not know the app window, the footer or the
    /// rail exist, so "right of the button" hung over the desktop and "Top" covered the status bar
    /// (2026-07-30). The pane is the one element bounded by all three.
    ///
    /// Placement is computed against an UNSCALED root, not the zoomed pane. With the pane as
    /// PlacementTarget, WPF multiplied a Popup's coordinates by the app size while a ContextMenu
    /// took a different path, so the two kinds of flyout landed in different places at any size
    /// but 100% (KillerScan).
    ///
    /// WIRING (at startup, and again when the sidebar moves to another edge):
    ///     FlyoutPlacement.UsePane(ContentPane, side);           // or UsePane(pane, root, side)
    /// then, each time a flyout opens:
    ///     FlyoutPlacement.Attach(historyMenu, historyButton);   // rises with its button
    ///     FlyoutPlacement.Attach(themeMenu, null);              // large menus: always the corner
    ///     themeMenu.IsOpen = true;
    ///
    /// The big menus (theme, language) always open in the corner whatever button opens them:
    /// raised to a button they would cover the pane they are meant to sit beside.
    ///
    /// The flyout's card carries a transparent margin for its drop shadow (FlyoutCard, or the
    /// ContextMenu template's MenuRoot); that halo is read from the card itself and taken off, so
    /// the VISIBLE card sits the eight pixels in. Do not add an inset of your own.
    /// </summary>
    internal static class FlyoutPlacement
    {
        private const double Inset = 8;
        private static readonly Thickness DefaultCardHalo = new(22, 18, 22, 26);

        private static FrameworkElement? _pane;
        private static FrameworkElement? _root;
        private static SidebarSide _side;

        /// <summary>The content pane. Set once at startup; every flyout positions against it.</summary>
        internal static void UsePane(FrameworkElement pane, SidebarSide side = SidebarSide.Left)
            => UsePane(pane, null, side);

        /// <summary>The content pane and the unscaled element placement is measured in (the
        /// window's root grid). Without a root, the window's content is used.</summary>
        internal static void UsePane(FrameworkElement pane, FrameworkElement? root, SidebarSide side = SidebarSide.Left)
        {
            _pane = pane;
            _root = root;
            _side = side;
        }

        private static FrameworkElement? Root =>
            _root ?? (_pane == null ? null : Window.GetWindow(_pane)?.Content as FrameworkElement) ?? _pane;

        internal static void Attach(Popup popup, UIElement? anchor)
        {
            popup.PlacementTarget = Root;
            popup.Placement = PlacementMode.Custom;
            popup.HorizontalOffset = popup.VerticalOffset = 0;
            Thickness halo = popup.Child is FrameworkElement card ? card.Margin : DefaultCardHalo;
            popup.CustomPopupPlacementCallback = (popupSize, _, _) => Place(popup, popupSize, halo, anchor);
        }

        internal static void Attach(ContextMenu menu, UIElement? anchor)
        {
            menu.PlacementTarget = Root;
            menu.Placement = PlacementMode.Custom;
            menu.HorizontalOffset = menu.VerticalOffset = 0;
            menu.ApplyTemplate();
            Thickness halo = menu.Template?.FindName("MenuRoot", menu) is FrameworkElement card ? card.Margin : DefaultCardHalo;
            menu.CustomPopupPlacementCallback = (popupSize, _, _) => Place(menu, popupSize, halo, anchor);
        }

        /// <summary>
        /// The callback's sizes are device pixels, so every DIP length is scaled by the root's own
        /// device ratio before it is combined with them.
        /// </summary>
        private static CustomPopupPlacement[] Place(FrameworkElement flyout, Size popupSize, Thickness halo, UIElement? anchor)
        {
            var root = Root;
            if (_pane == null || root == null || !_pane.IsDescendantOf(root))
                return [new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None)];

            var dpi = VisualTreeHelper.GetDpi(root);
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
            Rect pane = _pane.TransformToAncestor(root).TransformBounds(new Rect(_pane.RenderSize));
            Point Device(Point p) => new(p.X * sx, p.Y * sy);
            var topLeft = Device(pane.TopLeft);
            var bottomRight = Device(pane.BottomRight);
            double inX = Inset * sx, inY = Inset * sy;
            // The halo is in the flyout's own DIPs; the flyout can be scaled differently from the
            // root (app size), so measure its own ratio from the size WPF reports.
            double fx = flyout.ActualWidth > 0 ? popupSize.Width / flyout.ActualWidth : sx;
            double fy = flyout.ActualHeight > 0 ? popupSize.Height / flyout.ActualHeight : sy;

            double x = _side != SidebarSide.Right
                ? topLeft.X + inX - halo.Left * fx
                : bottomRight.X - popupSize.Width + halo.Right * fx - inX;

            // The corner: visible card's foot eight pixels above the pane's foot (or its top below
            // the pane's top for a top sidebar).
            double cornerY = _side == SidebarSide.Top
                ? topLeft.Y + inY - halo.Top * fy
                : bottomRight.Y - popupSize.Height + halo.Bottom * fy - inY;
            double y = cornerY;

            // Beside a left or right rail, rise with the button: the visible card's foot level with
            // the button's foot, never below the corner and never above the pane's top.
            if (anchor is FrameworkElement button && button.IsDescendantOf(root) && _side is SidebarSide.Left or SidebarSide.Right)
            {
                double buttonFoot = Device(button.TransformToAncestor(root).Transform(new Point(0, button.RenderSize.Height))).Y;
                double level = buttonFoot - popupSize.Height + halo.Bottom * fy;
                double highest = topLeft.Y + inY - halo.Top * fy;
                y = System.Math.Max(highest, System.Math.Min(cornerY, level));
            }
            // A flyout taller than the pane pins to its top rather than running over the toolbar.
            if (popupSize.Height - (halo.Top + halo.Bottom) * fy > bottomRight.Y - topLeft.Y)
                y = topLeft.Y - halo.Top * fy;
            return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
        }
    }
}
