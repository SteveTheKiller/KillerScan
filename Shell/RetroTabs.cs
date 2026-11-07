using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace KillerScan.Shell
{
    /// <summary>
    /// The 98SE look of the view tabs: Windows 98's own tab control, which KillerShell's
    /// FilePane draws with a template and this strip draws by hand. One light source, top-left:
    /// every tab has a white left edge and a #808080 then black right edge. The selected tab
    /// wears the page's face, is two pixels wider than its cell on each side so it overlaps its
    /// neighbours, and runs into the page; the others wear the darker TabInactiveBrush, sit two
    /// pixels shorter at both ends and stay behind it. Where the strip joins the page the page
    /// draws its own line across the strip, broken by the selected tab: a white top line with the
    /// tabs above (raised page), a black foot over a #808080 shade with the tabs below. The
    /// strip starts two pixels in, as Win98's does, so the selected end tab reaches the page edge.
    /// Separate from MainWindow so a test can lay a strip out and render it without a window.
    /// </summary>
    internal static class RetroTabs
    {
        internal const double Overhang = 2;
        internal const double Step = 2;
        internal const string PageLineTag = "retro-page-line", FootShadeTag = "retro-foot-shade", FootTag = "retro-foot";

        /// <summary>The strip's inset and the page's join lines, for one placement.</summary>
        internal static void ApplyBand(Grid band, UniformGrid strip, bool retro, bool bottom)
        {
            Panel.SetZIndex(strip, 2);
            strip.Margin = retro ? new Thickness(Overhang, 0, Overhang, 0) : new Thickness(0);
            var pageLine = Line(band, PageLineTag, "BevelLightBrush");
            var footShade = Line(band, FootShadeTag, "BevelDarkBrush");
            var foot = Line(band, FootTag, "BevelOuterDarkBrush");
            // Top strip: the page's white top line sits on the page's border row, the second row
            // of the strip's two-row overlap. Bottom strip: the #808080 shade takes that row and
            // the black foot the row after it, against the tabs.
            pageLine.Visibility = retro && !bottom ? Visibility.Visible : Visibility.Collapsed;
            pageLine.VerticalAlignment = VerticalAlignment.Bottom;
            pageLine.Margin = new Thickness(0, 0, 0, 1);
            footShade.Visibility = retro && bottom ? Visibility.Visible : Visibility.Collapsed;
            footShade.VerticalAlignment = VerticalAlignment.Top;
            footShade.Margin = new Thickness(0);
            foot.Visibility = footShade.Visibility;
            foot.VerticalAlignment = VerticalAlignment.Top;
            foot.Margin = new Thickness(0, 1, 0, 0);
        }

        private static Border Line(Grid band, string tag, string brushKey)
        {
            foreach (var child in band.Children)
                if (child is Border found && Equals(found.Tag, tag)) return found;
            var line = new Border { Tag = tag, Height = 1, IsHitTestVisible = false, SnapsToDevicePixels = true, UseLayoutRounding = true };
            line.SetResourceReference(Border.BackgroundProperty, brushKey);
            Panel.SetZIndex(line, 1);
            band.Children.Add(line);
            return line;
        }

        /// <summary>One tab's 98SE chrome. Called for every tab on every placement or theme
        /// change; with retro false it only clears what this set.</summary>
        internal static void StyleTab(ViewTab tab, bool retro, bool active, bool bottom)
        {
            tab.RetroTopEdges = retro && !bottom;
            tab.RetroBottomEdges = retro && bottom;
            Panel.SetZIndex(tab, retro && active ? 1 : 0);
            if (!retro) { tab.Margin = new Thickness(0); return; }
            tab.CornerRadius = new CornerRadius(0);
            tab.BorderThickness = new Thickness(0);
            tab.BorderBrush = null;
            tab.SetResourceReference(Border.BackgroundProperty, active ? "BackgroundBrush" : "TabInactiveBrush");
            tab.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            // The margin is layout, which the bottom strip's flip does not touch, and it is the
            // same both ways up: the selected tab overhangs its cell and runs the full row; the
            // others stop Step short at the page (where the page line shows through) and at the
            // outer edge (where they sit a step lower than the selected one).
            tab.Margin = active ? new Thickness(-Overhang, 0, -Overhang, 0) : new Thickness(0, Step, 0, Step);
        }

        /// <summary>
        /// A Win98 tab above the page: white left and top edge, #808080 then black down the
        /// right, the two top corners cut on the diagonal, and no foot, because the tab opens into
        /// the page (the selected one) or stops above the page line (the others). The kit's
        /// TabSurfaceBorder draws the bottom strip's edges; this is its top-strip counterpart.
        /// </summary>
        internal static Geometry? TopEdgesClip(FrameworkElement tab)
        {
            int w = (int)Math.Round(tab.RenderSize.Width), h = (int)Math.Round(tab.RenderSize.Height);
            if (w < 6 || h < 4) return null;
            Geometry shape = new RectangleGeometry(new Rect(0, 0, w, h));
            foreach (var corner in new[] { new Rect(0, 0, 2, 1), new Rect(0, 1, 1, 1), new Rect(w - 2, 0, 2, 1), new Rect(w - 1, 1, 1, 1) })
                shape = new CombinedGeometry(GeometryCombineMode.Exclude, shape, new RectangleGeometry(corner));
            shape.Freeze();
            return shape;
        }

        internal static void DrawTopEdges(DrawingContext dc, FrameworkElement tab)
        {
            int w = (int)Math.Round(tab.RenderSize.Width), h = (int)Math.Round(tab.RenderSize.Height);
            if (w < 6 || h < 4) return;
            Brush light = (tab.TryFindResource("BevelLightBrush") as Brush) ?? Brushes.White;
            Brush shadow = (tab.TryFindResource("BevelDarkBrush") as Brush) ?? Brushes.Gray;
            Brush dark = (tab.TryFindResource("BevelOuterDarkBrush") as Brush) ?? Brushes.Black;
            void Px(Brush b, int x, int y, int cw = 1, int ch = 1) => dc.DrawRectangle(b, null, new Rect(x, y, cw, ch));

            // The cut corners are a clip (TopEdgesClip), so the strip shows through them.
            Px(light, 0, 2, 1, h - 2);            // left highlight
            Px(light, 1, 1);                      // its diagonal into the top
            Px(light, 2, 0, w - 4, 1);            // top highlight
            Px(shadow, w - 2, 2, 1, h - 2);       // right, inner
            Px(dark, w - 1, 2, 1, h - 2);         // right, outer
            Px(shadow, w - 2, 1);                 // the shadow's diagonal
        }
    }
}
