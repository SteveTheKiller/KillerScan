using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private void RenderWorkspaceTabs(WorkspacePane pane)
        {
            pane.Strip.Children.Clear();
            bool retro = ThemeManager.Current == Theme.SE98;
            foreach (var tab in pane.Tabs)
            {
                bool active = tab == pane.Selected;
                string title = (tab.TitleKey == null ? tab.Title : Loc(tab.TitleKey)) + tab.TitleSuffix;
                var row = new DockPanel { LastChildFill = true };
                var label = new TextBlock
                {
                    Text = title, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontFamily = new FontFamily("Consolas"), FontSize = 11.5,
                    FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, active ? "TextBrush" : "MutedTextBrush");
                var close = new Button
                {
                    Content = "x", FocusVisualStyle = null,
                    FontFamily = new FontFamily("Consolas"),
                    ToolTip = Loc("Str_Workspace_Close") + " (Ctrl+W)"
                };
                close.SetResourceReference(StyleProperty, "TabCloseBtn");
                close.Click += (_, e) => { CloseWorkspaceTab(pane, tab); e.Handled = true; };
                DockPanel.SetDock(close, Dock.Right);
                row.Children.Add(close);
                row.Children.Add(label);

                var face = new Grid();
                var tabBorder = new Border
                {
                    Child = face, Tag = tab, MinWidth = 60, Cursor = Cursors.Hand, ToolTip = title,
                    CornerRadius = retro ? new CornerRadius(0) : new CornerRadius(6, 6, 0, 0),
                    Margin = new Thickness(0, 3, 0, 0),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(12, active ? 1 : 4, 5, 5),
                    BorderThickness = active
                        ? new Thickness(0, retro ? 2 : 3, 0, 0)
                        : new Thickness(0, 0, 1, 1)
                };
                tabBorder.SetResourceReference(Border.BorderBrushProperty,
                    active ? retro ? "BevelLightBrush" : "PrimaryBrush" : "PaneBorderBrush");
                if (active)
                {
                    // Match the surface directly below the tab, as the family pane tabs do.
                    tabBorder.SetResourceReference(Border.BackgroundProperty,
                        tab.Content is Controls.HistoryWorkspace ? "ScanContentPaneBrush" : "BackgroundBrush");
                    var texture = new Border
                    {
                        IsHitTestVisible = false,
                        Margin = new Thickness(-12, -1, -5, -5)
                    };
                    texture.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
                    texture.SetResourceReference(OpacityProperty, "GrainOpacity");
                    face.Children.Add(texture);
                }
                face.Children.Add(row);
                if (retro && active)
                {
                    var light = new Border
                    {
                        IsHitTestVisible = false, Margin = new Thickness(-12, -1, -5, -5),
                        BorderThickness = new Thickness(2, 0, 0, 0)
                    };
                    light.SetResourceReference(Border.BorderBrushProperty, "BevelLightBrush");
                    var dark = new Border
                    {
                        IsHitTestVisible = false, Margin = new Thickness(-12, -1, -5, -5),
                        BorderThickness = new Thickness(0, 0, 2, 0)
                    };
                    dark.SetResourceReference(Border.BorderBrushProperty, "BevelDarkBrush");
                    face.Children.Add(light);
                    face.Children.Add(dark);
                }
                tabBorder.SizeChanged += (_, _) => ClipWorkspaceTab(tabBorder);
                Panel.SetZIndex(tabBorder, active ? 1 : 0);
                tabBorder.MouseLeftButtonDown += (_, e) =>
                {
                    SelectWorkspaceTab(pane, tab);
                    e.Handled = true;
                };
                var menu = new ContextMenu();
                var move = new MenuItem { Header = Loc("Str_Workspace_Move") };
                move.Click += (_, _) => { SelectWorkspaceTab(pane, tab); MoveWorkspaceTab(); };
                var closeItem = new MenuItem { Header = Loc("Str_Workspace_Close"), InputGestureText = "Ctrl+W" };
                closeItem.Click += (_, _) => CloseWorkspaceTab(pane, tab);
                menu.Items.Add(move);
                menu.Items.Add(closeItem);
                tabBorder.ContextMenu = menu;
                pane.Strip.Children.Add(tabBorder);
            }
            UpdateWorkspacePaneAppearance(pane);
        }

        private void UpdateWorkspacePaneAppearance(WorkspacePane pane)
        {
            bool retro = ThemeManager.Current == Theme.SE98;
            pane.Frame.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            pane.Frame.BorderThickness = new Thickness(1, 0, 1, 1);
            pane.Frame.CornerRadius = retro ? new CornerRadius(0) : new CornerRadius(0, 0, 6, 6);
            pane.HeaderLine.SetResourceReference(Border.BackgroundProperty,
                retro ? "BevelLightBrush" : "PaneBorderBrush");
            pane.EmptyBackdrop.Visibility = pane.Selected == null ? Visibility.Visible : Visibility.Collapsed;
            foreach (var border in pane.Strip.Children.OfType<Border>())
            {
                bool active = border.Tag == pane.Selected;
                border.BorderThickness = active
                    ? new Thickness(0, retro ? 2 : 3, 0, 0)
                    : new Thickness(0, 0, 1, 1);
                border.SetResourceReference(Border.BorderBrushProperty,
                    active ? retro ? "BevelLightBrush" : "PrimaryBrush" : "PaneBorderBrush");
            }
            ClipWorkspacePaneBottom(pane);
        }

        private static void ClipWorkspacePaneBottom(WorkspacePane pane)
        {
            if (pane.Frame.Child is not FrameworkElement content) return;
            double width = content.ActualWidth, height = content.ActualHeight;
            if (width <= 0 || height <= 0) return;
            double radius = Math.Max(0, pane.Frame.CornerRadius.BottomRight - pane.Frame.BorderThickness.Right);
            radius = Math.Min(radius, Math.Min(width / 2, height));
            if (radius <= 0)
            {
                content.Clip = new RectangleGeometry(new Rect(0, 0, width, height));
                return;
            }
            var shape = new StreamGeometry();
            using (var path = shape.Open())
            {
                path.BeginFigure(new Point(0, 0), true, true);
                path.LineTo(new Point(width, 0), true, false);
                path.LineTo(new Point(width, height - radius), true, false);
                path.ArcTo(new Point(width - radius, height), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                path.LineTo(new Point(radius, height), true, false);
                path.ArcTo(new Point(0, height - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            shape.Freeze();
            content.Clip = shape;
        }

        private static void ClipWorkspaceTab(Border tab)
        {
            double width = tab.ActualWidth, height = tab.ActualHeight;
            if (width <= 0 || height <= 0) return;
            double radius = Math.Min(tab.CornerRadius.TopLeft, Math.Min(width / 2, height));
            if (radius <= 0) { tab.Clip = null; return; }
            var shape = new StreamGeometry();
            using (var path = shape.Open())
            {
                path.BeginFigure(new Point(0, height), true, true);
                path.LineTo(new Point(0, radius), true, false);
                path.ArcTo(new Point(radius, 0), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                path.LineTo(new Point(width - radius, 0), true, false);
                path.ArcTo(new Point(width, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                path.LineTo(new Point(width, height), true, false);
            }
            shape.Freeze();
            tab.Clip = shape;
        }
    }
}
