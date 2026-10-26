#nullable enable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerFamily.Layout
{
    /// <summary>
    /// A Grid that clips only when asked to. WPF also clips a panel laid out a fraction of a pixel
    /// smaller than it asked for, which the app zoom's LayoutTransform does at sizes like 110%, and
    /// that cuts a pane's drop shadow off at the panel's edge. Use it for every panel between the
    /// zoomed host and a shadowed pane. ClipToBounds still clips.
    /// </summary>
    public sealed class UnclippedGrid : Grid
    {
        protected override Geometry? GetLayoutClip(Size layoutSlotSize)
            => ClipToBounds ? base.GetLayoutClip(layoutSlotSize) : null;
    }
}
