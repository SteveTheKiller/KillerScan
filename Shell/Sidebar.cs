using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KillerScan.Controls;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        /// <summary>
        /// The history sidebar. KillerScan was the last app in the family whose rail had no
        /// panel behind it; this gives it one, following KillerNotes exactly. Collapsed, the
        /// lane is the rail's own width, so the window looks as it always did.
        /// </summary>
        private bool _sidebarCollapsed = true;

        /// <summary>
        /// Screen pixels, so a width the user dragged to keeps its on-screen size under app zoom.
        /// </summary>
        private double _sidebarBaseWidth = SidebarDefaultPx;

        /// <summary>
        /// False until the user drags the panel. Until then the default width is held in logical
        /// units, so it grows with app zoom along with the text and the longest saved-scan date
        /// still fits at any zoom.
        /// </summary>
        private bool _sidebarWidthSaved;
        private bool _sidebarRight;

        private double RailW => TryFindResource("RailColumnWidth") is GridLength width ? width.Value : 24;
        private const double PanelMinLogical = 140;
        private const double SidebarMinPx = 170;
        private const double SidebarDefaultPx = 176;
        private const double SidebarMaxPx = 480;

        // The default fits the widest date at full size. A width the user drags to is kept as is:
        // narrower rows draw their text up to 15% smaller before trimming it (ShrinkToFit).
        private double ExpandedLogicalWidth(double scale) =>
            _sidebarWidthSaved
                ? Math.Max(_sidebarBaseWidth / scale, PanelMinLogical)
                : Math.Max(SidebarDefaultPx, DateFitLogicalWidth());

        /// <summary>
        /// The narrowest panel, in logical units, that shows the widest possible saved-scan date in
        /// full at its normal size. Measured with the font the rows actually draw in, so the default
        /// width holds in every theme and at every app zoom.
        /// </summary>
        private double DateFitLogicalWidth()
        {
            string widest = new DateTimeOffset(2026, 12, 28, 22, 58, 0, TimeSpan.Zero)
                .ToString("g", CultureInfo.CurrentCulture);
            var row = HistoryList.Items.Count > 0
                ? HistoryList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                : null;
            TextBlock? date = row == null ? null : FirstTextBlock(row);
            var face = date != null
                ? new Typeface(date.FontFamily, date.FontStyle, date.FontWeight, date.FontStretch)
                : new Typeface(HistoryList.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            // The unshrunk size: a row already drawn smaller must not talk the default width down.
            double size = date != null ? ShrinkToFit.BaseSize(date) : 13;
            double text = new FormattedText(widest, CultureInfo.CurrentCulture, FlowDirection, face, size,
                Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
            // Row padding (9 each side), the list's own margins and border, and the scrollbar when
            // a long history shows one.
            double scrollbar = FirstScrollBar(HistoryList) is { Visibility: Visibility.Visible } bar ? bar.ActualWidth : 0;
            return Math.Ceiling(text + 18 + HistoryList.Margin.Left + HistoryList.Margin.Right + 4 + scrollbar);
        }

        private static TextBlock? FirstTextBlock(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock text) return text;
                if (FirstTextBlock(child) is TextBlock found) return found;
            }
            return null;
        }

        private static ScrollBar? FirstScrollBar(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollBar { Orientation: Orientation.Vertical } bar) return bar;
                if (FirstScrollBar(child) is ScrollBar found) return found;
            }
            return null;
        }

        private void InitSidebar()
        {
            var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
            if (TryFindResource(typeof(ContextMenu)) is Style style) menu.Style = style;
            var heading = new MenuItem { IsEnabled = false };
            heading.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Sidebar");
            menu.Items.Add(heading);
            foreach (var side in new[] { SidebarSide.Left, SidebarSide.Right })
            {
                var item = new MenuItem
                {
                    IsCheckable = true, Tag = side, Icon = side.MenuIcon(),
                    InputGestureText = side == SidebarSide.Right ? "Alt+Shift+Right" : "Alt+Shift+Left"
                };
                item.SetResourceReference(HeaderedItemsControl.HeaderProperty, side.StringKey());
                item.Click += (_, _) => SelectSidebarSide(side == SidebarSide.Right);
                menu.Items.Add(item);
            }
            SidebarToggleStrip.ContextMenu = menu;
            SidebarToggleBtn.ToolTipOpening += (_, _) =>
                SidebarToggleBtn.ToolTip = Loc(_sidebarCollapsed ? "Str_TT_ExpandSidebar" : "Str_TT_CollapseSidebar")
                    + (_sidebarRight ? " (Alt+Shift+Right)" : " (Alt+Shift+Left)");
            menu.Opened += (_, _) => UpdateSidebarSideMenu();
            SelectSidebarSide(string.Equals(App.GetSetting(SidebarSides.SettingName), "Right", StringComparison.OrdinalIgnoreCase), save: false);
            KillerScan.Services.ThemeManager.ThemeChanged += RefreshSidebarTheme;
            Closed += (_, _) => KillerScan.Services.ThemeManager.ThemeChanged -= RefreshSidebarTheme;
            // Always starts closed; a rail button opens it. Restoring the last open state brought
            // History back on every launch whatever was open before.
            _sidebarCollapsed = true;
            if (double.TryParse(App.GetSetting("HistorySidebarWidth"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double width))
            {
                _sidebarBaseWidth = Math.Min(SidebarMaxPx, Math.Max(SidebarMinPx, width));
                _sidebarWidthSaved = true;
            }
            ApplySidebarState();
        }

        private void SelectSidebarSide(bool right, bool save = true)
        {
            if (_sidebarRight != right)
            {
                var first = ScaleHost.ColumnDefinitions[0];
                ScaleHost.ColumnDefinitions.RemoveAt(0);
                ScaleHost.ColumnDefinitions.Add(first);
                foreach (UIElement child in ScaleHost.Children)
                    Grid.SetColumn(child, 2 - Grid.GetColumn(child) - Grid.GetColumnSpan(child));
            }
            _sidebarRight = right;
            if (save) App.SetSetting(SidebarSides.SettingName, right ? "Right" : "Left");
            RailButtons.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            SidebarToggleStrip.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            RailButtons.Margin = right ? new Thickness(0, 0, 2, 6) : new Thickness(2, 0, 0, 6);
            SidebarToggleBtn.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            SidebarResizeGrip.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            FlyoutPlacement.UsePane(DevicesPane, RootGrid, right ? SidebarSide.Right : SidebarSide.Left);
            UpdateSidebarSideMenu();
            RefreshSidebarTheme();
        }

        private void SidebarSideShortcut(bool right)
        {
            if (right == _sidebarRight) ToggleSidebar();
            else SelectSidebarSide(right);
        }

        private void UpdateSidebarSideMenu()
        {
            foreach (MenuItem item in SidebarToggleStrip.ContextMenu.Items)
                if (item.Tag is SidebarSide side)
                    item.IsChecked = side == (_sidebarRight ? SidebarSide.Right : SidebarSide.Left);
        }

        private void RefreshSidebarTheme()
        {
            SidebarClip.Margin = _sidebarRight ? new Thickness(RailW, 0, 0, 0) : new Thickness(0, 0, RailW, 0);
            SidebarResizeGrip.Margin = SidebarClip.Margin;
            ScaleHost.Resources.Remove("DevicesPaneMargin");
            if (_sidebarRight && Application.Current.TryFindResource("DevicesPaneMargin") is Thickness inset)
                ScaleHost.Resources["DevicesPaneMargin"] = new Thickness(inset.Right, inset.Top, inset.Left, inset.Bottom);
            ApplySidebarState();
        }

        // ---- Sections -----------------------------------------------------------------
        // The panel hosts one section at a time. Switching while it is open cross-fades in
        // place, because sliding the whole panel shut and open again to change what is in it
        // reads as the sidebar flinching.

        private string _sidebarSection = "history";

        /// <summary>
        /// Shows a section, opening the panel if it is shut. Pressing the rail button of the
        /// section already on screen closes the panel, so one control both opens and dismisses.
        /// </summary>
        private void ShowSidebarSection(string section)
        {
            if (!_sidebarCollapsed && _sidebarSection == section) { ToggleSidebar(); return; }
            bool fade = !_sidebarCollapsed;
            SetSidebarSection(section, fade);
            OpenSidebar();
        }

        private void SetSidebarSection(string section, bool fade)
        {
            FrameworkElement ListFor(string name) => name switch
            {
                "locations" => LocationsList,
                _ => HistoryList
            };
            var incoming = ListFor(section);
            var outgoing = ListFor(_sidebarSection);
            _sidebarSection = section;
            SidebarSectionIcon.Text = ((char)(section switch { "locations" => 0xE707, _ => 0xE81C })).ToString();
            SidebarHeading.SetResourceReference(TextBlock.TextProperty,
                section switch { "locations" => "Str_Loc_ManageTitle", _ => "Str_History_Title" });
            LocationsFooter.Visibility = section == "locations" ? Visibility.Visible : Visibility.Collapsed;
            // Every list but the incoming one is hidden, whichever was showing before.
            foreach (var list in new FrameworkElement[] { HistoryList, LocationsList })
                if (list != incoming && list != outgoing) list.Visibility = Visibility.Collapsed;
            RefreshSidebarSection();
            UpdateSidebarRailTags();

            if (!fade)
            {
                outgoing.Visibility = Visibility.Collapsed;
                incoming.Opacity = 1;
                incoming.Visibility = Visibility.Visible;
                return;
            }
            Anim.FadeOut(outgoing, () =>
            {
                outgoing.Visibility = Visibility.Collapsed;
                incoming.Visibility = Visibility.Visible;
                Anim.FadeIn(incoming);
            });
        }

        private void RefreshSidebarSection()
        {
            if (_sidebarSection == "locations") RefreshLocationsList();
            else RefreshHistoryList();
        }

        /// <summary>Lights the rail button whose section is showing.</summary>
        private void UpdateSidebarRailTags()
        {
            bool open = !_sidebarCollapsed;
            HistoryButton.Tag = open && _sidebarSection == "history" ? "on" : null;
            LocationsButton.Tag = open && _sidebarSection == "locations" ? "on" : null;
        }

        private void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

        private void ToggleSidebar()
        {
            _sidebarCollapsed = !_sidebarCollapsed;
            App.SetSetting("HistorySidebarOpen", _sidebarCollapsed ? "0" : "1");
            ApplySidebarState(animate: true);
        }

        /// <summary>Opens the sidebar if it is shut. Used by the history entry points.</summary>
        private void OpenSidebar()
        {
            if (!_sidebarCollapsed) return;
            _sidebarCollapsed = false;
            App.SetSetting("HistorySidebarOpen", "1");
            ApplySidebarState(animate: true);
        }

        private void ApplySidebarState(bool animate = false)
        {
            // The chevron points where the panel is going, per the family standard. Set from a
            // char code, never a literal glyph: pasted PUA characters have been corrupted by
            // tooling in this family before.
            SidebarToggleBtn.Content = ((char)(_sidebarCollapsed != _sidebarRight ? 0xE76C : 0xE76B)).ToString();
            SidebarToggleBtn.SetResourceReference(ToolTipProperty,
                _sidebarCollapsed ? "Str_TT_ExpandSidebar" : "Str_TT_CollapseSidebar");
            SidebarResizeGrip.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;

            double scale = _appScale <= 0 ? 1 : _appScale;
            double target = _sidebarCollapsed ? RailW : ExpandedLogicalWidth(scale) + RailW;

            if (!_sidebarCollapsed)
            {
                HistorySidebar.Visibility = Visibility.Visible;
                // Fill the panel whenever it opens, including a restore at startup. The refresh
                // is guarded against raising selection, so this shows the list without dragging
                // the workspace over to the history view.
                RefreshSidebarSection();
            }
            UpdateSidebarRailTags();

            if (!animate)
            {
                SidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, null);
                SidebarCol.Width = new GridLength(target);
                HistorySidebar.ClearValue(WidthProperty);
                HistorySidebar.HorizontalAlignment = HorizontalAlignment.Stretch;
                if (_sidebarCollapsed) HistorySidebar.Visibility = Visibility.Collapsed;
                return;
            }

            // Freeze the panel at its open width against the outside edge, so the closing tween wipes
            // it behind the clip instead of reflowing its contents into a sliver.
            double frozen = ExpandedLogicalWidth(scale);
            HistorySidebar.Width = frozen;
            HistorySidebar.HorizontalAlignment = _sidebarRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;

            var anim = new GridLengthAnimation
            {
                From = SidebarCol.ActualWidth,
                To = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                EasingFunction = new QuadraticEase
                { EasingMode = _sidebarCollapsed ? EasingMode.EaseIn : EasingMode.EaseOut }
            };
            anim.Completed += (_, _) =>
            {
                SidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, null);
                SidebarCol.Width = new GridLength(target);
                HistorySidebar.ClearValue(WidthProperty);
                HistorySidebar.HorizontalAlignment = HorizontalAlignment.Stretch;
                if (_sidebarCollapsed) HistorySidebar.Visibility = Visibility.Collapsed;
            };
            SidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        /// <summary>Re-applies the width after an app-zoom change, which moves the logical size.</summary>
        internal void RefreshSidebarWidth()
        {
            if (_sidebarCollapsed) return;
            double scale = _appScale <= 0 ? 1 : _appScale;
            SidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, null);
            SidebarCol.Width = new GridLength(ExpandedLogicalWidth(scale) + RailW);
        }

        private void SidebarResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            // Driven straight off the pointer with no tween; a tween here reads as lag.
            double scale = _appScale <= 0 ? 1 : _appScale;
            double next = Math.Min(SidebarMaxPx, Math.Max(SidebarMinPx,
                (SidebarCol.ActualWidth - RailW + (_sidebarRight ? -e.HorizontalChange : e.HorizontalChange)) * scale));
            _sidebarBaseWidth = next;
            _sidebarWidthSaved = true;
            SidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, null);
            SidebarCol.Width = new GridLength(next / scale + RailW);
        }

        private void SidebarResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e) =>
            App.SetSetting("HistorySidebarWidth", _sidebarBaseWidth.ToString("0.##", CultureInfo.InvariantCulture));
    }
}
