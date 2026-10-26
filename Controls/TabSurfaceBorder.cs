using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerScan.Controls
{
    /// <summary>Joins a selected tab's face to its content as the intervening menu collapses.</summary>
    public class TabSurfaceBorder : Border
    {
        protected override void OnRender(DrawingContext drawingContext)
        {
            // The right-only divider shares the next tab's left outline, rather than
            // sitting one pixel inside the preceding tab. Child content stays in place.
            // Not on a 98SE bottom strip, whose edges are drawn on the tab's true bounds below.
            bool dividerOnly = BorderThickness == new Thickness(0, 0, 1, 0) && !RetroBottomEdges;
            if (dividerOnly) drawingContext.PushTransform(new TranslateTransform(1, 0));
            if (RetroBottomEdges || !DrawRoundedTop(drawingContext)) base.OnRender(drawingContext);
            if (RetroBottomEdges) DrawRetroBottomEdges(drawingContext);
            if (dividerOnly) drawingContext.Pop();
        }

        /// <summary>
        /// The active tab: its outline rounds the top corners at CornerRadius and the face inside
        /// rounds them 1px less, so the face nests in the outline. A plain Border works the inner
        /// curve out from each side's thickness, which under the 3px top stripe flattened the
        /// face's corners almost square. False for any other tab, which Border draws as before.
        /// </summary>
        private bool DrawRoundedTop(DrawingContext dc)
        {
            CornerRadius r = CornerRadius;
            Thickness b = BorderThickness;
            if ((r.TopLeft <= 0 && r.TopRight <= 0) || r.BottomLeft > 0 || r.BottomRight > 0 || b.Top <= 0) return false;
            var outer = new Rect(RenderSize);
            if (outer.Width <= b.Left + b.Right || outer.Height <= b.Top + b.Bottom) return false;
            var inner = new Rect(b.Left, b.Top, outer.Width - b.Left - b.Right, outer.Height - b.Top - b.Bottom);
            Geometry outerShape = TopRounded(outer, r.TopLeft, r.TopRight);
            Geometry face = TopRounded(inner, Math.Max(0, r.TopLeft - 1), Math.Max(0, r.TopRight - 1));
            // On a single pane the outline's sides fade out toward the tab's foot, and the pane's
            // own edge only starts below the tab, which left a gap between the two. The pane's
            // edge runs on up the sides beneath the outline, so the accent fades into it.
            if (DividerBrush is Brush edge && BorderBrush is not SolidColorBrush)
            {
                if (b.Left > 0) dc.DrawRectangle(edge, null, new Rect(0, r.TopLeft, b.Left, outer.Height - r.TopLeft));
                if (b.Right > 0)
                    dc.DrawRectangle(edge, null, new Rect(outer.Width - b.Right, r.TopRight, b.Right, outer.Height - r.TopRight));
            }
            if (BorderBrush != null)
                dc.DrawGeometry(BorderBrush, null, new CombinedGeometry(GeometryCombineMode.Exclude, outerShape, face));
            if (Background != null) dc.DrawGeometry(Background, null, face);
            return true;
        }

        /// <summary>
        /// The active tab's grain sits in the child and reaches past the padding to cover the
        /// whole face, so it kept square corners over the rounded face. Clipping the child to the
        /// face (in the child's own coordinates) rounds the grain with it; the icon, title and
        /// close button sit inside the padding and are untouched.
        /// </summary>
        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Size arranged = base.ArrangeOverride(arrangeSize);
            if (Child is UIElement child)
            {
                CornerRadius r = CornerRadius;
                Thickness b = BorderThickness, p = Padding;
                bool rounded = !RetroBottomEdges && (r.TopLeft > 0 || r.TopRight > 0) &&
                               r.BottomLeft <= 0 && r.BottomRight <= 0 && b.Top > 0;
                double w = arranged.Width - b.Left - b.Right, h = arranged.Height - b.Top - b.Bottom;
                child.Clip = rounded && w > 0 && h > 0
                    ? TopRounded(new Rect(-p.Left, -p.Top, w, h), Math.Max(0, r.TopLeft - 1), Math.Max(0, r.TopRight - 1))
                    : null;
            }
            return arranged;
        }

        private static Geometry TopRounded(Rect rect, double left, double right)
        {
            left = Math.Min(left, Math.Min(rect.Width / 2, rect.Height));
            right = Math.Min(right, Math.Min(rect.Width / 2, rect.Height));
            var shape = new StreamGeometry();
            using (StreamGeometryContext c = shape.Open())
            {
                c.BeginFigure(new Point(rect.Left, rect.Bottom), isFilled: true, isClosed: true);
                c.LineTo(new Point(rect.Left, rect.Top + left), true, false);
                if (left > 0) c.ArcTo(new Point(rect.Left + left, rect.Top), new Size(left, left), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(rect.Right - right, rect.Top), true, false);
                if (right > 0) c.ArcTo(new Point(rect.Right, rect.Top + right), new Size(right, right), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(rect.Right, rect.Bottom), true, false);
            }
            shape.Freeze();
            return shape;
        }

        /// <summary>
        /// 98SE with the tabs below the page. The template's bevel borders are zeroed for this case
        /// (TabPlacement.cs) and the edges are drawn here pixel by pixel, because Win98 cuts a tab's
        /// foot corners on the diagonal and stacked borders cannot. Light comes from the top-left
        /// as everywhere else in Win98: white left edge, #808080 then black on the right and foot,
        /// with the shadow owning both foot corners. No top edge: the tab hangs from the page.
        /// </summary>
        private void DrawRetroBottomEdges(DrawingContext dc)
        {
            int w = (int)Math.Round(RenderSize.Width), h = (int)Math.Round(RenderSize.Height);
            if (w < 6 || h < 4) return;
            // The template flips this border upside down for a bottom strip; draw in screen
            // orientation by flipping back.
            dc.PushTransform(new MatrixTransform(1, 0, 0, -1, 0, h));
            Brush light = (TryFindResource("BevelLightBrush") as Brush) ?? Brushes.White;
            Brush shadow = (TryFindResource("BevelDarkBrush") as Brush) ?? Brushes.Gray;
            Brush dark = (TryFindResource("BevelOuterDarkBrush") as Brush) ?? Brushes.Black;
            Brush face = (TryFindResource("BackgroundBrush") as Brush) ?? Brushes.Silver;
            void Px(Brush b, int x, int y, int cw = 1, int ch = 1) => dc.DrawRectangle(b, null, new Rect(x, y, cw, ch));

            // The selected tab is part of the page, so it wears the page's face rather than the
            // listing's: the listing keeps its own sunken edge inside the page above it.
            if (UsesContentBackground && TryFindResource("MenuBackgroundBrush") is Brush page)
                Px(page, 0, 0, w, h);

            // Cut corners show the strip behind the tab.
            Px(face, 0, h - 2, 1, 2); Px(face, 1, h - 1);
            Px(face, w - 1, h - 2, 1, 2); Px(face, w - 2, h - 1);
            Px(light, 0, 0, 1, h - 2);            // left highlight
            Px(light, 1, h - 2);                  // its diagonal into the foot
            Px(shadow, w - 2, 0, 1, h - 2);       // right, inner
            Px(dark, w - 1, 0, 1, h - 2);         // right, outer
            Px(shadow, 2, h - 2, w - 4, 1);       // foot, inner
            Px(dark, 2, h - 1, w - 4, 1);         // foot, outer
            Px(dark, w - 2, h - 2);               // the shadow's diagonal
            dc.Pop();
        }

        public static readonly DependencyProperty RetroBottomEdgesProperty = DependencyProperty.Register(
            nameof(RetroBottomEdges), typeof(bool), typeof(TabSurfaceBorder),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        public bool RetroBottomEdges
        {
            get => (bool)GetValue(RetroBottomEdgesProperty);
            set => SetValue(RetroBottomEdgesProperty, value);
        }

        public static readonly DependencyProperty DividerBrushProperty = DependencyProperty.Register(
            nameof(DividerBrush), typeof(Brush), typeof(TabSurfaceBorder), new PropertyMetadata(null, RefreshDivider));
        private static readonly DependencyPropertyKey FadedDividerBrushPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(FadedDividerBrush), typeof(Brush), typeof(TabSurfaceBorder), new PropertyMetadata(null));
        public static readonly DependencyProperty FadedDividerBrushProperty = FadedDividerBrushPropertyKey.DependencyProperty;
        public Brush? FadedDividerBrush => (Brush?)GetValue(FadedDividerBrushProperty);
        public Brush? DividerBrush
        {
            get => (Brush?)GetValue(DividerBrushProperty);
            set => SetValue(DividerBrushProperty, value);
        }

        private static void RefreshDivider(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var border = (TabSurfaceBorder)sender;
            Brush? brush = border.DividerBrush;
            if (brush is SolidColorBrush solid)
            {
                var color = solid.Color;
                var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0));
                fade.GradientStops.Add(new GradientStop(color, 0.75));
                fade.Freeze();
                brush = fade;
            }
            border.SetValue(FadedDividerBrushPropertyKey, brush);
        }

        public static readonly DependencyProperty MenuBackgroundProperty = DependencyProperty.Register(
            nameof(MenuBackground), typeof(Brush), typeof(TabSurfaceBorder),
            new PropertyMetadata(Brushes.Transparent, Refresh));
        public static readonly DependencyProperty ContentBackgroundProperty = DependencyProperty.Register(
            nameof(ContentBackground), typeof(Brush), typeof(TabSurfaceBorder),
            new PropertyMetadata(null, Refresh));
        public static readonly DependencyProperty BlendProgressProperty = DependencyProperty.Register(
            nameof(BlendProgress), typeof(double), typeof(TabSurfaceBorder),
            new PropertyMetadata(0.0, Refresh));
        private static readonly DependencyPropertyKey UsesContentBackgroundPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(UsesContentBackground), typeof(bool), typeof(TabSurfaceBorder), new PropertyMetadata(false));
        public static readonly DependencyProperty UsesContentBackgroundProperty = UsesContentBackgroundPropertyKey.DependencyProperty;
        public bool UsesContentBackground => (bool)GetValue(UsesContentBackgroundProperty);

        public Brush MenuBackground
        {
            get => (Brush)GetValue(MenuBackgroundProperty);
            set => SetValue(MenuBackgroundProperty, value);
        }

        public Brush? ContentBackground
        {
            get => (Brush?)GetValue(ContentBackgroundProperty);
            set => SetValue(ContentBackgroundProperty, value);
        }

        public double BlendProgress
        {
            get => (double)GetValue(BlendProgressProperty);
            set => SetValue(BlendProgressProperty, value);
        }

        private static void Refresh(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var border = (TabSurfaceBorder)sender;
            Brush menu = border.MenuBackground;
            Brush? content = border.ContentBackground;
            double progress = Math.Max(0, Math.Min(1, border.BlendProgress));
            border.SetValue(UsesContentBackgroundPropertyKey,
                content != null && (progress >= 0.5 || ReferenceEquals(menu, content)));
            if (content == null || progress == 0) { border.Background = menu; return; }
            if (progress == 1) { border.Background = content; return; }

            // Composite brushes rather than animating shared theme resources. This also keeps
            // gradient brushes intact, and leaves the tab's border, grain and children alone.
            var area = new RectangleGeometry(new Rect(0, 0, 1, 1));
            var overlay = new DrawingGroup { Opacity = progress };
            overlay.Children.Add(new GeometryDrawing(content, null, area));
            var layers = new DrawingGroup();
            layers.Children.Add(new GeometryDrawing(menu, null, area));
            layers.Children.Add(overlay);
            // Freezing this drawing would recursively freeze its borrowed theme brushes.
            border.Background = new DrawingBrush(layers);
        }
    }
}
