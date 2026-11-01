using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KillerScan.Controls;
using KillerFamily.Accessibility;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        // The five views are permanent tabs joined to the content pane, below it by default and
        // above it on request (KillerShell's placement choice, same strings and shortcuts). The
        // active tab wears the pane's own fill and edge, open on the side it shares with the pane,
        // so the pane and its tab read as one sheet; the pane squares its corners on that side.
        private const double TabRadius = 3.0;   // KillerShell Shell/Tabs.cs
        private readonly Dictionary<string, Border> _viewTabs = [];
        // One row, equal shares: the tabs span the pane's full width, as KillerShell's do.
        private readonly System.Windows.Controls.Primitives.UniformGrid _viewTabStrip = new()
        {
            Rows = 1, UseLayoutRounding = true, SnapsToDevicePixels = true
        };
        private bool _tabsAtBottom = true;

        // The strip sits in a band that carries KillerShell's TabBandShadow (Controls/FilePane.xaml):
        // a shadow rising from the pane, darkest at the pane's edge and feathered out at both
        // ends, so the tabs read as sitting in front of it. The active tab's opaque face covers it.
        private readonly Grid _viewTabBand = new() { UseLayoutRounding = true };
        private readonly Border _viewTabBandShadow = new() { IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };

        private void BuildTabBandShadow()
        {
            var fall = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fall.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0));
            fall.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.45));
            fall.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, 0, 0, 0), 1));
            fall.Freeze();
            var ends = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            ends.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0));
            ends.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 255, 255, 255), 0.06));
            ends.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 255, 255, 255), 0.94));
            ends.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1));
            ends.Freeze();
            _viewTabBandShadow.Background = fall;
            _viewTabBandShadow.OpacityMask = ends;
            // 0 on a flat theme, so 98SE's strip stays flat.
            _viewTabBandShadow.SetResourceReference(UIElement.OpacityProperty, "BarShadowOpacity");
            _viewTabBand.Children.Add(_viewTabBandShadow);
            _viewTabBand.Children.Add(_viewTabStrip);
        }

        private void BuildWorkspaceNavigation()
        {
            _tabsAtBottom = App.GetSetting("TabsAtBottom") != "0";
            AddViewTab("scan", "Str_View_Devices", "", "F6 / Ctrl+T", () => ShowScanView("devices"));
            AddViewTab("services", "Str_Services_Title", "", "F7", () => ServicesButton_Click(this, new RoutedEventArgs()));
            AddViewTab("topology", "Str_View_Topology", "", "F8", () => ShowScanView("topology"));
            AddViewTab("watch", "Str_View_KeepAlive", "", "F9", () => Watch_Click(this, new RoutedEventArgs()));
            AddViewTab("terminal", "Str_Workspace_Terminal", "", "F10 / Ctrl+Shift+T", NewTerminalView);
            // Above the pane and its sunken-edge layers, so the active tab's fill covers the pane
            // border where the two join.
            BuildTabBandShadow();
            Panel.SetZIndex(_viewTabBand, 12);
            _viewTabBand.ContextMenu = new ContextMenu();
            _viewTabBand.ContextMenu.Opened += (_, _) => BuildTabPlacementMenu(_viewTabBand.ContextMenu);
            WorkspaceHost.Children.Add(_viewTabBand);
            BuildTabScrollShadow();
            ApplyTabPlacement();
            Services.ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(
                new Action(ApplyTabPlacement), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void AddViewTab(string view, string key, string glyph, string shortcut, Action action)
        {
            var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            label.SetResourceReference(TextBlock.TextProperty, key);
            label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            // Icon at the tab's left edge, title centred in the whole tab.
            var content = new Grid { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new Glyph
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left
            });
            content.Children.Add(label);
            // The grain layer sits under the content; only the active tab shows it, because its
            // opaque fill hides the row's shared grain.
            var grainLayer = new Border { IsHitTestVisible = false, Opacity = 0, Margin = new Thickness(-14, -6, -16, -6) };
            grainLayer.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
            // The scroll shadow's share of the active tab: above the fill and grain, below the icon
            // and title, and inside the accent ring (TabSurfaceBorder clips the child to the face).
            var shadeLayer = new Border { IsHitTestVisible = false, Margin = grainLayer.Margin, Visibility = Visibility.Collapsed,
                RenderTransformOrigin = new Point(0.5, 0.5) };
            shadeLayer.SetResourceReference(UIElement.OpacityProperty, "EdgeFadeOpacity");
            var body = new Grid();
            body.Children.Add(grainLayer);
            body.Children.Add(shadeLayer);
            body.Children.Add(content);
            var tab = new ViewTab(action, () => _workspaceView == view)
            {
                Child = body, Padding = new Thickness(14, 6, 16, 6), MinWidth = 0,
                Cursor = Cursors.Hand, Focusable = true, Tag = view
            };
            tab.SetResourceReference(TextElement.ForegroundProperty, "MutedTextBrush");
            tab.SetResourceReference(TabSurfaceBorder.DividerBrushProperty, "PaneEdgeBrush");
            tab.RenderTransformOrigin = new Point(0.5, 0.5);
            content.RenderTransformOrigin = new Point(0.5, 0.5);
            tab.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, key);
            var tip = new TextBlock();
            var caption = new System.Windows.Documents.Run();
            caption.SetResourceReference(System.Windows.Documents.Run.TextProperty, key);
            tip.Inlines.Add(caption);
            tip.Inlines.Add(" (" + shortcut + ")");
            tab.ToolTip = tip;
            tab.MouseLeftButtonUp += (_, e) => { action(); e.Handled = true; };
            tab.KeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Enter or Key.Space)) return;
                action(); e.Handled = true;
            };
            _viewTabs.Add(view, tab);
            _viewTabStrip.Children.Add(tab);
        }

        private static readonly Transform FlipVertical = (Transform)new ScaleTransform(1, -1).GetAsFrozen();

        private void StyleViewTab(Border border)
        {
            // KillerShell's tab, on the kit's TabSurfaceBorder (Controls/TabSurfaceBorder.cs). The
            // active tab is the page's own fill and grain, ringed in TabActiveRingFadeBrush at
            // TabStripeThickness, with no shadow over the join: accent across the outer edge, fading down the sides into the
            // pane edge drawn beneath them, so the tab and the page are one sheet. Inactive tabs
            // are bare, split by a faded PaneEdgeBrush divider, with no hover state. The control
            // only knows a top strip, so a bottom strip is the same tab drawn upside down with
            // its contents turned back (KillerShell's TabPlacementTransform).
            var tab = (TabSurfaceBorder)border;
            bool active = (string)tab.Tag == _workspaceView;
            bool bottom = _tabsAtBottom;
            var grid = (Grid)tab.Child;
            var grain = (Border)grid.Children[0];
            var shade = (UIElement)grid.Children[1];
            var content = (UIElement)grid.Children[2];
            tab.RenderTransform = bottom ? FlipVertical : Transform.Identity;
            content.RenderTransform = bottom ? FlipVertical : Transform.Identity;
            shade.RenderTransform = bottom ? FlipVertical : Transform.Identity;
            int index = _viewTabStrip.Children.IndexOf(tab);
            int last = _viewTabStrip.Children.Count - 1;
            if (active)
            {
                tab.CornerRadius = new CornerRadius(TabRadius, TabRadius, 0, 0);
                // On top of a table (Devices, Services) the tab joins the column header, not the
                // page, so it takes the header's colour and the two read as one band.
                bool onHeader = !bottom && (string)tab.Tag is "scan" or "services";
                tab.SetResourceReference(Border.BackgroundProperty, onHeader ? "TableHeaderBrush" : "ScanContentPaneBrush");
                tab.SetResourceReference(Border.BorderBrushProperty, "TabActiveRingFadeBrush");
                tab.SetResourceReference(Border.BorderThicknessProperty, "TabStripeThickness");
                tab.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
                // The column header is matte, so a tab joined to it is too.
                if (onHeader) grain.Opacity = 0;
                else grain.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
            }
            else
            {
                tab.CornerRadius = new CornerRadius(0);
                tab.Background = Brushes.Transparent;
                tab.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(TabSurfaceBorder.FadedDividerBrush)) { Source = tab });
                tab.BorderThickness = index == last ? new Thickness(0) : new Thickness(0, 0, 1, 0);
                tab.SetResourceReference(TextElement.ForegroundProperty, "MutedTextBrush");
                grain.Opacity = 0;
            }
        }

        private void BuildTabPlacementMenu(ContextMenu menu)
        {
            menu.Items.Clear();
            void AddChoice(bool bottom)
            {
                var item = new MenuItem { IsCheckable = true, IsChecked = _tabsAtBottom == bottom,
                    InputGestureText = bottom ? "Alt+Shift+Down" : "Alt+Shift+Up" };
                item.SetResourceReference(HeaderedItemsControl.HeaderProperty, bottom ? "Str_Layout_TabsBottom" : "Str_Layout_TabsTop");
                item.Click += (_, _) => SetTabPlacement(bottom);
                menu.Items.Add(item);
            }
            AddChoice(false);
            AddChoice(true);
        }

        internal void SetTabPlacement(bool bottom)
        {
            if (_tabsAtBottom == bottom) return;
            _tabsAtBottom = bottom;
            App.SetSetting("TabsAtBottom", bottom ? "1" : "0");
            ApplyTabPlacement();
        }

        /// <summary>
        /// Puts the strip on its side of the pane and squares the pane's corners there. With the
        /// tabs on top the table's header loses its top rounding, since the tabs sit on it; with
        /// them below, the header keeps it and the pane's foot squares instead.
        /// </summary>
        private void ApplyTabPlacement()
        {
            Grid.SetRow(_viewTabBand, _tabsAtBottom ? 4 : 2);
            _viewTabBand.VerticalAlignment = _tabsAtBottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            // Darkest against the pane: at the band's foot for a top strip, its head for a bottom one.
            _viewTabBandShadow.RenderTransform = _tabsAtBottom ? FlipVertical : Transform.Identity;
            UpdateWorkspaceNavigation();
        }

        /// <summary>
        /// KillerShell's TabSideCorners (Shell/Tabs.cs): the pane keeps its full radius on the side
        /// away from the tabs. On the tab side a corner is square where the active tab is the end
        /// tab, so the pane's edge runs straight up into the tab, and TabRadius anywhere else.
        /// </summary>
        private void ApplyPaneCorners()
        {
            double r = PaneRadius(), t = Math.Min(TabRadius, r);
            bool left = _workspaceView == (string)((Border)_viewTabStrip.Children[0]).Tag;
            bool right = _workspaceView == (string)((Border)_viewTabStrip.Children[^1]).Tag;
            double l = left ? 0 : t, rt = right ? 0 : t;
            var corners = _tabsAtBottom ? new CornerRadius(r, r, rt, l) : new CornerRadius(l, rt, r, r);
            DevicesPane.CornerRadius = corners;
            DevicesPaneShadow.CornerRadius = corners;
            // Every view draws its own frame and grain inside the pane from these two keys, so
            // they are overridden for the pane's subtree and square on the tab side with it.
            TerminalLayout.Resources["PanelCornerRadius"] = corners;
            TerminalLayout.Resources["PaneGrainCornerRadius"] = corners;
            // The strip overlaps the pane's edge so the active tab's fill covers that line.
            // 2px, not 1: the strip scales with the app size, and at a fractional size a 1px overlap
            // lands between device pixels and the pane edge shows through under the active tab.
            _viewTabBand.Margin = _tabsAtBottom ? new Thickness(0, -2, 0, 0) : new Thickness(0, 0, 0, -2);
            ClipWorkspaceSurface();
        }

        private double PaneRadius() => TryFindResource("PaneCornerRadiusValue") is double d && d >= 0 ? d : 6.0;

        private void UpdateWorkspaceNavigation()
        {
            foreach (var tab in _viewTabs.Values) StyleViewTab(tab);
            ApplyPaneCorners();
        }

        /// <summary>
        /// The address box of each view bar that has one. Its width is set by the fit, so the
        /// fit measures the bar as if the box were at its preferred width; measuring it at the
        /// width the last fit left it made every squeeze compound on the one before.
        /// </summary>
        private readonly Dictionary<string, FrameworkElement> _viewToolbarTargets = [];
        private const double TargetPreferredWidth = 220;
        private const double TargetFloor = 40;

        /// <summary>
        /// Gives a view's address box whatever the bar has left, between its floor and its
        /// preferred width. The view buttons used to share this bar; the tabs took them off it,
        /// so the box only has the view's own controls to fit beside. The scan bar also fits its
        /// filter box, so it finishes that itself.
        /// </summary>
        private void FitToolbarViews()
        {
            if (_workspaceToolbar.ActualWidth <= 0) return;
            string key = _viewToolbars.ContainsKey(_workspaceView) ? _workspaceView : "scan";
            if (key == "scan" || !_viewToolbarTargets.TryGetValue(key, out var target)
                || !_viewToolbars.TryGetValue(key, out var toolbar)) return;
            var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
            toolbar.InvalidateMeasure();
            toolbar.Measure(unbounded);
            double others = toolbar.DesiredSize.Width + toolbar.Margin.Left + toolbar.Margin.Right
                - (double.IsNaN(target.Width) ? target.DesiredSize.Width : target.Width);
            target.Width = Math.Max(TargetFloor, Math.Min(TargetPreferredWidth, _workspaceToolbar.ActualWidth - others));
        }

        // ── The bottom scroll shadow ─────────────────────────────────────────────────────────
        // KillerShell's TabScrollShadow (Controls/FilePane.xaml, FilePane.xaml.cs): with the tabs
        // below, the active tab continues the page, so the shade that says "more below" is ONE
        // gradient, transparent 28px above the pane's foot and darkest at the active tab's foot,
        // clipped to the list's foot plus the tab's face. It sits above the band so the tab's
        // icon and title darken with the rows. The list's own bottom cue steps aside while it
        // shows, so the two never stack. Hidden at the end of the list and with the tabs on top.

        private const double ScrollShadowReach = 28;
        private readonly Border _tabScrollShadow = new()
        {
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed
        };
        private ScrollViewer? _shadowScroller;

        private void BuildTabScrollShadow()
        {
            _tabScrollShadow.SetResourceReference(UIElement.OpacityProperty, "EdgeFadeOpacity");
            Grid.SetRow(_tabScrollShadow, 3);
            Grid.SetRowSpan(_tabScrollShadow, 2);
            Panel.SetZIndex(_tabScrollShadow, 30);
            WorkspaceHost.Children.Add(_tabScrollShadow);
            WorkspaceHost.LayoutUpdated += (_, _) => UpdateTabScrollShadow();
        }

        /// <summary>The tallest vertical scroller in the active view: the list the shadow belongs to.</summary>
        private ScrollViewer? ActiveListScroller()
        {
            ScrollViewer? best = null;
            void Walk(DependencyObject node)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is UIElement { IsVisible: false }) continue;
                    if (child is ScrollViewer sv && sv.ComputedVerticalScrollBarVisibility == Visibility.Visible
                        && (best == null || sv.ActualHeight > best.ActualHeight)) best = sv;
                    Walk(child);
                }
            }
            Walk(TerminalLayout);
            return best;
        }

        private string? _shadowScrollerView;
        private (double, double, double, double, double, double, Border?) _shadowShape;

        private void UpdateTabScrollShadow()
        {
            // LayoutUpdated fires after every layout pass in the window, so the visual tree is walked
            // for the list's scroller only when the view changed or the one found before left the screen.
            var scroller = !_tabsAtBottom ? null
                : _shadowScroller is { IsVisible: true } known && _shadowScrollerView == _workspaceView ? known
                : ActiveListScroller();
            _shadowScrollerView = _workspaceView;
            if (!ReferenceEquals(scroller, _shadowScroller))
            {
                if (_shadowScroller != null)
                {
                    _shadowScroller.ScrollChanged -= ShadowScroller_ScrollChanged;
                    _shadowScroller.Resources.Remove("ScrollBottomCueBrush");
                }
                _shadowScroller = scroller;
                if (scroller != null) scroller.ScrollChanged += ShadowScroller_ScrollChanged;
            }
            _viewTabs.TryGetValue(_workspaceView, out var face);
            bool on = scroller != null && scroller.VerticalOffset < scroller.ScrollableHeight - 0.5
                && DevicesPane.ActualHeight > 0 && face != null && face.ActualHeight > 0;
            if (scroller != null)
            {
                bool hidden = scroller.Resources.Contains("ScrollBottomCueBrush");
                if (on && !hidden) scroller.Resources["ScrollBottomCueBrush"] = Brushes.Transparent;
                else if (!on && hidden) scroller.Resources.Remove("ScrollBottomCueBrush");
            }
            var visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (_tabScrollShadow.Visibility != visibility) _tabScrollShadow.Visibility = visibility;
            foreach (var each in _viewTabs.Values)
                if (((Grid)each.Child).Children[1] is Border idle && (!on || !ReferenceEquals(each, face))
                    && idle.Visibility != Visibility.Collapsed) idle.Visibility = Visibility.Collapsed;
            if (!on) { _shadowShape = default; return; }

            // Measured from the real positions, relative to the shadow's own grid row.
            Point origin = new(0, WorkspaceHost.RowDefinitions[0].ActualHeight + WorkspaceHost.RowDefinitions[1].ActualHeight
                + WorkspaceHost.RowDefinitions[2].ActualHeight);
            Rect pane = DevicesPane.TransformToAncestor(WorkspaceHost).TransformBounds(new Rect(DevicesPane.RenderSize));
            Rect tab = face!.TransformToAncestor(WorkspaceHost).TransformBounds(new Rect(face.RenderSize));
            double top = pane.Bottom - ScrollShadowReach, bottom = Math.Max(pane.Bottom, tab.Bottom);

            // The list's part: an overlay over the pane's foot only.
            Thickness stroke = DevicesPane.BorderThickness;
            CornerRadius radius = DevicesPane.CornerRadius;
            Geometry clip = Controls.PickerBorder.RoundedGeometry(
                new Rect(pane.X + stroke.Left, 0, Math.Max(0, pane.Width - stroke.Left - stroke.Right), ScrollShadowReach),
                new Size(0, 0), new Size(0, 0),
                new Size(Math.Max(0, radius.BottomRight - stroke.Right / 2), radius.BottomRight),
                new Size(Math.Max(0, radius.BottomLeft - stroke.Left / 2), radius.BottomLeft));
            clip.Freeze();
            double span = bottom - top;
            // Unchanged geometry keeps the brushes it already has instead of building new ones each pass.
            var shape = (top, span, pane.X, pane.Width, tab.Top, tab.Height, (Border?)face);
            if (shape.Equals(_shadowShape)) return;
            _shadowShape = shape;
            var margin = new Thickness(0, top - origin.Y, 0, 0);
            if (_tabScrollShadow.Margin != margin) _tabScrollShadow.Margin = margin;
            if (_tabScrollShadow.Height != ScrollShadowReach) _tabScrollShadow.Height = ScrollShadowReach;
            if (_tabScrollShadow.Clip?.Bounds != clip.Bounds) _tabScrollShadow.Clip = clip;
            // The overlay covers only the first part of the one gradient, so its stops are scaled
            // to end where the pane does; the tab's layer picks up from there.
            _tabScrollShadow.Background = ShadowSlice(0, ScrollShadowReach, span);

            // The tab's part: its own layer between the fill and the label, in screen orientation
            // (it is flipped back like the label), painted with the same gradient in place.
            if (((Grid)face.Child).Children[1] is Border shade)
            {
                shade.Visibility = Visibility.Visible;
                Rect layer = shade.TransformToAncestor(WorkspaceHost).TransformBounds(new Rect(shade.RenderSize));
                shade.Background = ShadowSlice(layer.Top - top, layer.Height, span);
            }
        }

        /// <summary>
        /// One slice of the scroll shadow's single gradient (KillerShell's stops: clear, 0x38 at
        /// half, 0x80 at the foot), for an element whose top sits <paramref name="offset"/> px into
        /// a gradient <paramref name="span"/> px tall and which is <paramref name="height"/> px tall.
        /// </summary>
        private static Brush ShadowSlice(double offset, double height, double span)
        {
            var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, -offset), EndPoint = new Point(0, span - offset) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0, 0, 0), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, 0, 0, 0), 1));
            brush.Freeze();
            return brush;
        }

        private void ShadowScroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateTabScrollShadow();

        private void NewTerminalView()
        {
            if (_terminalControl?.IsManaged == true)
            {
                ShowWorkspaceContent(_terminalControl, "terminal");
                _terminalControl.Focus();
            }
            else NewTerminal();
        }
    }

    /// <summary>A view tab (Devices, Services, Topology, Keep Alive, Terminal). Its peer gives
    /// screen readers the tab role, which tab is showing, and a way to switch to it.</summary>
    internal sealed class ViewTab : Controls.TabSurfaceBorder
    {
        internal ViewTab(Action activate, Func<bool> isActive)
        {
            _activate = activate;
            _isActive = isActive;
            FocusVisualStyle = AccessibleControls.FocusStyleFor(this);
        }

        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ViewTabPeer(this);

        private sealed class ViewTabPeer(ViewTab tab) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(tab),
            System.Windows.Automation.Provider.ISelectionItemProvider
        {
            protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
                System.Windows.Automation.Peers.AutomationControlType.TabItem;
            protected override string GetClassNameCore() => "ViewTab";
            protected override bool IsKeyboardFocusableCore() => true;
            protected override string GetNameCore() => System.Windows.Automation.AutomationProperties.GetName(tab);
            public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface patternInterface) =>
                patternInterface == System.Windows.Automation.Peers.PatternInterface.SelectionItem ? this : base.GetPattern(patternInterface);

            public bool IsSelected => tab._isActive();
            public System.Windows.Automation.Provider.IRawElementProviderSimple? SelectionContainer => null;
            public void Select() => tab._activate();
            public void AddToSelection() => tab._activate();
            public void RemoveFromSelection() { }
        }

        private readonly Action _activate;
        private readonly Func<bool> _isActive;
    }
}
