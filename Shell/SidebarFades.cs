using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        // ---- Sidebar list edge fades ----
        // The KillerNotes notes-list cue: a list's own rows fade to transparent at an edge while
        // more content lies past it, so the chrome and grain behind the list show through and
        // the fade always matches the surface. A theme that sets EdgeFadeOpacity to 0 (98SE)
        // ends its lists at a hard edge instead.

        private const double SidebarFadePx = 28;

        private void InitSidebarFades()
        {
            foreach (var list in new[] { HistoryList })
            {
                // ScrollChanged fires on scroll, when the list is refilled and when the viewport
                // resizes, so it covers every case once the list is on screen.
                list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(SidebarList_ScrollChanged));
                list.Loaded += (_, _) =>
                    Dispatcher.BeginInvoke(new Action(() => UpdateSidebarFade(list)), DispatcherPriority.Loaded);
            }
        }

        private void SidebarList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (sender is not ListBox list) return;
            // Only the list's own ScrollViewer counts, never one nested inside a row.
            if (!ReferenceEquals(e.OriginalSource, FindDescendant<ScrollViewer>(list))) return;
            UpdateSidebarFade(list);
        }

        /// <summary>Re-evaluates both sidebar lists, for a theme change.</summary>
        private void UpdateSidebarFades()
        {
            UpdateSidebarFade(HistoryList);
        }

        private static void UpdateSidebarFade(ListBox list)
        {
            list.OpacityMask = null;
            if (FindDescendant<ScrollViewer>(list) is not ScrollViewer scroll || list.ActualHeight <= 0)
            {
                return;
            }
            if (scroll.Template.FindName("PART_ScrollContentPresenter", scroll)
                    is not ScrollContentPresenter viewport) return;
            if (Application.Current?.TryFindResource("EdgeFadeOpacity") is double themeFade && themeFade <= 0)
            {
                viewport.OpacityMask = null;
                return;
            }

            bool overflow = scroll.ScrollableHeight > 0.5;
            bool fadeTop = overflow && scroll.VerticalOffset > 0.5;
            bool fadeBottom = overflow && scroll.VerticalOffset < scroll.ScrollableHeight - 0.5;
            if (!fadeTop && !fadeBottom)
            {
                viewport.OpacityMask = null;
                return;
            }

            double edge = Math.Min(0.45, SidebarFadePx / Math.Max(1, viewport.ActualHeight));
            var mask = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, viewport.ActualHeight)
            };
            if (fadeTop)
            {
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                mask.GradientStops.Add(new GradientStop(Colors.Black, edge));
            }
            else mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
            if (fadeBottom)
            {
                mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            }
            else mask.GradientStops.Add(new GradientStop(Colors.Black, 1));
            mask.Freeze();
            viewport.OpacityMask = mask;
        }
    }
}
