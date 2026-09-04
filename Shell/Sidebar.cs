using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        /// Screen pixels, so the panel keeps a constant on-screen width under app zoom. Opens
        /// narrow by default: the entries are short and the pane beside them is the point.
        /// </summary>
        private double _sidebarBaseWidth = SidebarMinPx;

        private const double RailW = 24;
        private const double PanelMinLogical = 140;
        private const double SidebarMinPx = 170;
        private const double SidebarMaxPx = 480;

        private double ExpandedLogicalWidth(double scale) =>
            Math.Max(_sidebarBaseWidth / scale, PanelMinLogical);

        private void InitSidebar()
        {
            _sidebarCollapsed = App.GetSetting("HistorySidebarOpen") != "1";
            if (double.TryParse(App.GetSetting("HistorySidebarWidth"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double width))
                _sidebarBaseWidth = Math.Min(SidebarMaxPx, Math.Max(SidebarMinPx, width));
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
            var incoming = section == "profiles" ? (FrameworkElement)ProfilesList : HistoryList;
            var outgoing = section == "profiles" ? (FrameworkElement)HistoryList : ProfilesList;
            _sidebarSection = section;
            SidebarHeading.SetResourceReference(TextBlock.TextProperty,
                section == "profiles" ? "Str_Profiles_Title" : "Str_History_Title");
            SaveProfileButton.Visibility = section == "profiles" ? Visibility.Visible : Visibility.Collapsed;
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
            if (_sidebarSection == "profiles") RefreshProfilesList();
            else RefreshHistoryList();
        }

        /// <summary>Lights the rail button whose section is showing, on both rails.</summary>
        private void UpdateSidebarRailTags()
        {
            bool open = !_sidebarCollapsed;
            object? history = open && _sidebarSection == "history" ? "on" : null;
            object? profiles = open && _sidebarSection == "profiles" ? "on" : null;
            HistoryButton.Tag = FixedHistoryButton.Tag = history;
            ProfilesButton.Tag = FixedProfilesButton.Tag = profiles;
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
            SidebarToggleBtn.Content = ((char)(_sidebarCollapsed ? 0xE76C : 0xE76B)).ToString();
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
                PlaceFixedRail(target);
                if (_sidebarCollapsed) HistorySidebar.Visibility = Visibility.Collapsed;
                return;
            }

            // Freeze the panel at its open width and left-align it, so the closing tween wipes
            // it behind the clip instead of reflowing its contents into a sliver.
            double frozen = ExpandedLogicalWidth(scale);
            HistorySidebar.Width = frozen;
            HistorySidebar.HorizontalAlignment = HorizontalAlignment.Left;

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
                PlaceFixedRail(target);
                if (_sidebarCollapsed) HistorySidebar.Visibility = Visibility.Collapsed;
            };
            SidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        /// <summary>
        /// The 98SE rail lives outside the zoom host, so it cannot sit in the sidebar column and
        /// has to be offset by hand to stay on the panel's inner lip.
        /// </summary>
        private void PlaceFixedRail(double laneWidth) =>
            FixedRail.Margin = new Thickness(Math.Max(0, laneWidth - RailW), 0, 0, 10);

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
                (SidebarCol.ActualWidth - RailW + e.HorizontalChange) * scale));
            _sidebarBaseWidth = next;
            SidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, null);
            SidebarCol.Width = new GridLength(next / scale + RailW);
        }

        private void SidebarResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e) =>
            App.SetSetting("HistorySidebarWidth", _sidebarBaseWidth.ToString("0.##", CultureInfo.InvariantCulture));
    }
}
