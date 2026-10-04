using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace KillerScan.Controls
{
    // ── Reordering pinned places ─────────────────────────────────────────────
    //
    // A pinned place can be dragged up or down among the other pinned places; Quick Access
    // entries and drives are not the user's list and stay where they are. The row moves live
    // under the pointer, and the new order is saved on release. Pins whose folder is missing
    // right now are not shown, so they keep their place at the end of the saved list instead
    // of being dropped.
    public partial class FileDialog
    {
        private PickerPlace? _pinDragPlace;
        private Point _pinDragStart;
        private bool _pinDragging;
        private bool _pinNavigatingWas;

        private void Places_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pinDragPlace = ItemUnder<PickerPlace>(e.OriginalSource as DependencyObject) is { Pinned: true } p ? p : null;
            _pinDragStart = e.GetPosition(PlacesList);
            _pinDragging = false;
        }

        private void Places_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_pinDragPlace == null || e.LeftButton != MouseButtonState.Pressed) return;
            Point now = e.GetPosition(PlacesList);
            if (!_pinDragging)
            {
                if (Math.Abs(now.Y - _pinDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _pinDragging = true;
                // A ListBox selects whatever row a held button passes over, and selecting a place
                // navigates. Hold navigation off for the whole drag and re-mark the current folder
                // when it ends.
                _pinNavigatingWas = _navigating;
                _navigating = true;
                PlacesList.CaptureMouse();
                DragCursors.BeginDrag();
            }

            var target = ItemUnder<PickerPlace>(PlacesList.InputHitTest(now) as DependencyObject);
            if (target is { Pinned: true } && !ReferenceEquals(target, _pinDragPlace))
            {
                int from = Places.IndexOf(_pinDragPlace), to = Places.IndexOf(target);
                if (from >= 0 && to >= 0)
                    Places.Move(from, to);
            }
            e.Handled = true;
        }

        private void Places_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_pinDragging)
            {
                e.Handled = true;
                SavePinOrder();
            }
            EndPinDrag();
        }

        private void Places_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_pinDragging) SavePinOrder();
            EndPinDrag();
        }

        private void EndPinDrag()
        {
            bool captured = _pinDragging;
            _pinDragPlace = null;
            _pinDragging = false;
            if (!captured) return;
            if (PlacesList.IsMouseCaptured) PlacesList.ReleaseMouseCapture();
            _navigating = _pinNavigatingWas;
            SyncPlacesSelection();
            DragCursors.EndDrag();
        }

        private void SavePinOrder()
        {
            var shown = Places.Where(p => p.Pinned).Select(p => p.Path).ToList();
            var hidden = PinnedPaths().Where(p => !shown.Any(s =>
                s.TrimEnd('\\').Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
            App.SetSetting(PinnedKey, string.Join("|", shown.Concat(hidden)));
        }
    }
}
