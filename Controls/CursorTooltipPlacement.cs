#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerFamily.Layout
{
    public static class CursorTooltipPlacement
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(CursorTooltipPlacement), new PropertyMetadata(false, EnabledChanged));
        private static readonly ConditionalWeakTable<ToolTip, PlacementState> States = new ConditionalWeakTable<ToolTip, PlacementState>();

        public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

        private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
        {
            if (!(target is ToolTip tip)) return;
            if ((bool)args.NewValue)
            {
                tip.Opened += Opened;
                tip.Closed += Closed;
            }
            else
            {
                tip.Opened -= Opened;
                tip.Closed -= Closed;
                Restore(tip);
            }
        }

        private static void Opened(object sender, RoutedEventArgs args)
        {
            var tip = (ToolTip)sender;
            if (States.TryGetValue(tip, out _) || !(tip.PlacementTarget is UIElement target)
                || !target.IsMouseOver || (tip.Placement != PlacementMode.Mouse && tip.Placement != PlacementMode.MousePoint)
                || !TryCursorBounds(out Rect screenBounds)) return;
            Rect anchor = Rect.Empty;
            try
            {
                anchor.Union(target.PointFromScreen(screenBounds.TopLeft));
                anchor.Union(target.PointFromScreen(screenBounds.TopRight));
                anchor.Union(target.PointFromScreen(screenBounds.BottomLeft));
                anchor.Union(target.PointFromScreen(screenBounds.BottomRight));
            }
            catch (InvalidOperationException) { return; }
            var state = new PlacementState(tip);
            States.Add(tip, state);
            tip.SetCurrentValue(ToolTip.PlacementRectangleProperty, anchor);
            tip.SetCurrentValue(ToolTip.VerticalOffsetProperty, state.VerticalOffset + 4);
            // Bottom placement also gives WPF its normal above/edge fallbacks around the
            // measured cursor rectangle instead of assuming the standard cursor size.
            tip.SetCurrentValue(ToolTip.PlacementProperty, PlacementMode.Bottom);
        }

        private static void Closed(object sender, RoutedEventArgs args) => Restore((ToolTip)sender);

        private static void Restore(ToolTip tip)
        {
            if (!States.TryGetValue(tip, out PlacementState? state) || state == null) return;
            States.Remove(tip);
            tip.SetCurrentValue(ToolTip.PlacementProperty, state.Placement);
            tip.SetCurrentValue(ToolTip.PlacementRectangleProperty, state.Rectangle);
            tip.SetCurrentValue(ToolTip.VerticalOffsetProperty, state.VerticalOffset);
        }

        private sealed class PlacementState
        {
            internal readonly PlacementMode Placement;
            internal readonly Rect Rectangle;
            internal readonly double VerticalOffset;
            internal PlacementState(ToolTip tip)
            {
                Placement = tip.Placement;
                Rectangle = tip.PlacementRectangle;
                VerticalOffset = tip.VerticalOffset;
            }
        }

        private static bool TryCursorBounds(out Rect bounds)
        {
            bounds = Rect.Empty;
            var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0 || cursor.Handle == IntPtr.Zero
                || !GetIconInfo(cursor.Handle, out IconInfo icon)) return false;
            try
            {
                IntPtr resource = icon.Color != IntPtr.Zero ? icon.Color : icon.Mask;
                if (resource == IntPtr.Zero || GetObjectInfo(resource, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap size) == 0) return false;
                int width = size.Width, height = icon.Color != IntPtr.Zero ? size.Height : size.Height / 2;
                if (width < 1 || height < 1 || width > 1024 || height > 1024) return false;
                byte[]? black = RenderCursor(cursor.Handle, width, height, 0);
                byte[]? white = RenderCursor(cursor.Handle, width, height, 255);
                if (black == null || white == null) return false;
                int left = width, top = height, right = -1, bottom = -1;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int index = (y * width + x) * 4;
                        if (black[index] == 0 && black[index + 1] == 0 && black[index + 2] == 0
                            && white[index] == 255 && white[index + 1] == 255 && white[index + 2] == 255) continue;
                        left = Math.Min(left, x); top = Math.Min(top, y);
                        right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                    }
                if (right < left || bottom < top) return false;
                bounds = new Rect(cursor.Position.X - icon.HotX + left, cursor.Position.Y - icon.HotY + top,
                    right - left + 1, bottom - top + 1);
                return true;
            }
            finally
            {
                if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
                if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
            }
        }

        private static byte[]? RenderCursor(IntPtr cursor, int width, int height, byte background)
        {
            byte[] pixels = new byte[checked(width * height * 4)];
            for (int index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = pixels[index + 1] = pixels[index + 2] = background;
                pixels[index + 3] = 255;
            }
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero), bitmap = IntPtr.Zero, previous = IntPtr.Zero;
            if (dc == IntPtr.Zero) return null;
            try
            {
                var header = new DibHeader { Size = (uint)Marshal.SizeOf<DibHeader>(), Width = width, Height = -height,
                    Planes = 1, Bits = 32, ImageSize = (uint)pixels.Length };
                bitmap = CreateDIBSection(dc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;
                previous = SelectObject(dc, bitmap);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return null;
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                if (!DrawIconEx(dc, 0, 0, cursor, width, height, 0, IntPtr.Zero, 3) || !GdiFlush()) return null;
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                return pixels;
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                DeleteDC(dc);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public IntPtr Handle; public NativePoint Position; }
        [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public int HotX, HotY; public IntPtr Mask, Color; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { public int Type, Width, Height, Stride; public ushort Planes, Bits; public IntPtr Data; }
        [StructLayout(LayoutKind.Sequential)] private struct DibHeader { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPels, YPels; public uint Colors, Important; }
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo state);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr cursor, out IconInfo info);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr cursor, int width, int height, uint step, IntPtr brush, uint flags);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObjectInfo(IntPtr bitmap, int size, out NativeBitmap info);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref DibHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    }
}
