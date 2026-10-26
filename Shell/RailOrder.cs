#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerScan.Shell
{
    /// <summary>
    /// The family rail order. Theme, language, shortcuts (?) and, in an app that has one, the
    /// settings gear sit at the rail's edge, the end away from the collapse chevron, reading in
    /// that order outward from the edge. They never move, so muscle memory carries from app to
    /// app. Every other rail button can be dragged into any order, and the order is saved.
    ///
    /// Each button keeps its own spacing, so a group gap travels with the button it follows.
    /// A strip that mirrors its rail (the icons reading outward from the middle) is read either
    /// way round. Flipping (the rail menu's Move icons to the other side) reverses the buttons
    /// and turns each gap with them, so the edge buttons stay outermost at the other end; the
    /// app moves the stack and the chevron to their swapped ends.
    /// Elements on the rail that are not registered stay where they are.
    /// </summary>
    internal sealed class RailOrder
    {
        private readonly Panel _rail;
        private readonly FrameworkElement[] _edge;
        private readonly FrameworkElement[] _movable;
        private readonly Action<string> _save;
        private FrameworkElement? _pressed;
        private Point _pressPoint;
        private bool _dragging;

        /// <summary>True while the edge buttons sit at the rail's other end.</summary>
        internal bool Flipped { get; private set; }

        /// <summary>
        /// Reverses the rail so the edge buttons, still outermost, read from the other end. Each
        /// registered button's gap turns with it, so a group gap stays between the same two
        /// buttons. The saved order is unaffected.
        /// </summary>
        internal void SetFlipped(bool flipped)
        {
            if (flipped == Flipped) return;
            Flipped = flipped;
            bool horizontal = Horizontal();
            var shown = Shown();
            foreach (FrameworkElement button in shown)
            {
                Thickness m = button.Margin;
                button.Margin = horizontal ? new Thickness(m.Right, m.Top, m.Left, m.Bottom)
                    : new Thickness(m.Left, m.Bottom, m.Right, m.Top);
            }
            shown.Reverse();
            Place(shown, null);
        }

        /// <summary>Raised after a drag changes the order, once it has been saved.</summary>
        internal event Action? Changed;

        private RailOrder(Panel rail, FrameworkElement[] edgeFirst, FrameworkElement[] movable, Action<string> save)
        {
            _rail = rail;
            _edge = edgeFirst;
            _movable = movable;
            _save = save;
        }

        /// <summary>
        /// Puts the rail in the family order, restores a saved order of the other buttons and
        /// makes them draggable. <paramref name="edgeFirst"/> lists theme, language, shortcuts and
        /// settings (when the app has it) starting from the edge. Every button needs a Name,
        /// which is what the saved order records.
        /// </summary>
        internal static RailOrder Attach(Panel rail, IReadOnlyList<FrameworkElement> edgeFirst,
            IReadOnlyList<FrameworkElement> movable, string? saved, Action<string> save)
        {
            foreach (FrameworkElement element in edgeFirst.Concat(movable))
                if (string.IsNullOrEmpty(element.Name))
                    throw new ArgumentException("Every rail button needs a Name for the saved order.");
            var order = new RailOrder(rail, edgeFirst.ToArray(), movable.ToArray(), save);
            order.Restore(saved);
            foreach (FrameworkElement button in order._movable)
            {
                button.PreviewMouseLeftButtonDown += order.OnPress;
                button.PreviewMouseMove += order.OnMove;
                button.PreviewMouseLeftButtonUp += order.OnRelease;
                button.LostMouseCapture += order.OnLostCapture;
            }
            return order;
        }

        /// <summary>
        /// The rail from the chevron end to the edge: the movable buttons in their saved order,
        /// then the edge buttons with the first of them outermost. Saved names that are not
        /// movable buttons are ignored, and a button the saved order does not mention keeps its
        /// default place.
        /// </summary>
        internal static string[] Arrange(IReadOnlyList<string> movable, IReadOnlyList<string> edgeFirst, string? saved)
        {
            var known = new HashSet<string>(movable, StringComparer.Ordinal);
            var order = new List<string>();
            foreach (string part in (saved ?? "").Split(','))
            {
                string name = part.Trim();
                if (known.Contains(name) && !order.Contains(name)) order.Add(name);
            }
            for (int i = 0; i < movable.Count; i++)
                if (!order.Contains(movable[i])) order.Insert(Math.Min(i, order.Count), movable[i]);
            order.AddRange(edgeFirst.Reverse());
            return order.ToArray();
        }

        /// <summary>The saved form of the movable buttons' order, from the chevron end.</summary>
        internal static string Serialize(IEnumerable<string> movable) => string.Join(",", movable);

        /// <summary>The slot whose center is nearest the pointer, or -1 when there are none.</summary>
        internal static int NearestSlot(IReadOnlyList<double> centers, double pointer)
        {
            int best = -1;
            double distance = double.MaxValue;
            for (int i = 0; i < centers.Count; i++)
            {
                double d = Math.Abs(centers[i] - pointer);
                if (d < distance) { distance = d; best = i; }
            }
            return best;
        }

        // True when the rail currently runs from the edge (theme first), as a mirrored strip does.
        private bool Mirrored()
        {
            int theme = _rail.Children.IndexOf(_edge[0]);
            return _movable.Length > 0 && _movable.All(m => _rail.Children.IndexOf(m) > theme);
        }

        // The registered buttons in the order the rail shows them.
        private List<FrameworkElement> Shown()
            => _rail.Children.OfType<FrameworkElement>().Where(e => _edge.Contains(e) || _movable.Contains(e)).ToList();

        private void Restore(string? saved)
        {
            var byName = _edge.Concat(_movable).ToDictionary(e => e.Name, StringComparer.Ordinal);
            var names = Arrange(_movable.Select(m => m.Name).ToList(), _edge.Select(e => e.Name).ToList(), saved);
            IEnumerable<FrameworkElement> wanted = names.Select(n => byName[n]);
            if (Mirrored()) wanted = wanted.Reverse();
            Place(wanted.ToList(), null);
        }

        // Puts the registered buttons into the rail's registered positions in the given order.
        // The kept element is never taken off the rail, so a button being dragged keeps the mouse.
        private void Place(List<FrameworkElement> wanted, FrameworkElement? keep)
        {
            var children = _rail.Children.OfType<UIElement>().ToList();
            var slots = new List<int>();
            for (int i = 0; i < children.Count; i++)
                if (children[i] is FrameworkElement e && (_edge.Contains(e) || _movable.Contains(e))) slots.Add(i);
            if (slots.Count != wanted.Count) return;

            var final = new List<UIElement>(children);
            for (int j = 0; j < slots.Count; j++) final[slots[j]] = wanted[j];
            if (final.SequenceEqual(children)) return;

            for (int i = _rail.Children.Count - 1; i >= 0; i--)
                if (!ReferenceEquals(_rail.Children[i], keep)) _rail.Children.RemoveAt(i);
            int kept = keep == null ? -1 : final.IndexOf(keep);
            for (int i = 0; i < final.Count; i++)
            {
                if (i == kept) continue;
                if (kept >= 0 && i < kept) _rail.Children.Insert(i, final[i]);
                else _rail.Children.Add(final[i]);
            }
        }

        private bool Horizontal()
        {
            if (_rail is StackPanel stack) return stack.Orientation == Orientation.Horizontal;
            if (_movable.Length < 2) return false;
            Point a = Center(_movable[0]), b = Center(_movable[1]);
            return Math.Abs(a.X - b.X) > Math.Abs(a.Y - b.Y);
        }

        private Point Center(FrameworkElement element)
            => element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), _rail);

        private void OnPress(object sender, MouseButtonEventArgs e)
        {
            _pressed = sender as FrameworkElement;
            _pressPoint = e.GetPosition(_rail);
            _dragging = false;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_pressed == null || !ReferenceEquals(sender, _pressed)) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _pressed = null; return; }
            Point point = e.GetPosition(_rail);
            if (!_dragging)
            {
                if (Math.Abs(point.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(point.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _dragging = true;
                if (!_pressed.IsMouseCaptured) _pressed.CaptureMouse();
                _pressed.Opacity = 0.55;
                ToolTipService.SetIsEnabled(_pressed, false);
            }

            bool horizontal = Horizontal();
            var shown = Shown();
            var movable = shown.Where(s => _movable.Contains(s)).ToList();
            var centers = movable.Select(m => horizontal ? Center(m).X : Center(m).Y).ToList();
            int target = NearestSlot(centers, horizontal ? point.X : point.Y);
            int current = movable.IndexOf(_pressed);
            if (target < 0 || target == current) return;

            movable.RemoveAt(current);
            movable.Insert(target, _pressed);
            int next = 0;
            var wanted = shown.Select(s => _movable.Contains(s) ? movable[next++] : s).ToList();
            Place(wanted, _pressed);
        }

        private void OnRelease(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) { _pressed = null; return; }
            e.Handled = true;   // a drag is not a click
            Finish();
        }

        private void OnLostCapture(object sender, MouseEventArgs e)
        {
            if (_dragging) Finish();
        }

        private void Finish()
        {
            FrameworkElement? dragged = _pressed;
            _dragging = false;
            _pressed = null;
            if (dragged == null) return;
            dragged.ClearValue(UIElement.OpacityProperty);
            dragged.ClearValue(ToolTipService.IsEnabledProperty);
            if (dragged.IsMouseCaptured) dragged.ReleaseMouseCapture();
            IEnumerable<string> names = Shown().Where(s => _movable.Contains(s)).Select(s => s.Name);
            if (Mirrored()) names = names.Reverse();
            _save(Serialize(names));
            Changed?.Invoke();
        }
    }
}
