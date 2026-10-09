using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerScan.Controls
{
    /// <summary>Renders the app artwork once for its device-pixel footprint.</summary>
    public sealed class AppIcon : Image
    {
        private const string MasterUri = "pack://application:,,,/Resources/app-icon-master.png";
        private static BitmapSource? master;
        private static readonly ConditionalWeakTable<BitmapSource, RasterCache> rasters =
            new();
        private Rect lastPixels = Rect.Empty;

        private sealed class RasterCache
        {
            internal readonly Dictionary<long, BitmapSource> Images = [];
        }

        public AppIcon()
        {
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
            Loaded += (_, _) => { LayoutUpdated += OnLayoutUpdated; InvalidateVisual(); };
            Unloaded += (_, _) => LayoutUpdated -= OnLayoutUpdated;
        }

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            GetFootprint(out Rect pixels, out _, out _, out _);
            if (pixels != lastPixels) InvalidateVisual();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            lastPixels = Rect.Empty;
            InvalidateVisual();
        }

        private void GetFootprint(out Rect pixels, out Point origin, out double sx, out double sy)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            sx = dpi.DpiScaleX;
            sy = dpi.DpiScaleY;
            origin = new Point();
            var presentation = PresentationSource.FromVisual(this);
            if (presentation?.RootVisual is Visual root && root != this)
            {
                var toRoot = TransformToAncestor(root);
                var device = presentation.CompositionTarget.TransformToDevice;
                origin = device.Transform(toRoot.Transform(new Point()));
                var x = device.Transform(toRoot.Transform(new Point(1, 0)));
                var y = device.Transform(toRoot.Transform(new Point(0, 1)));
                sx = Math.Abs(x.X - origin.X);
                sy = Math.Abs(y.Y - origin.Y);
            }
            if (sx <= 0 || sy <= 0) { pixels = Rect.Empty; return; }
            double left = Math.Round(origin.X, MidpointRounding.AwayFromZero);
            double top = Math.Round(origin.Y, MidpointRounding.AwayFromZero);
            double width = Math.Round(RenderSize.Width * sx, MidpointRounding.AwayFromZero);
            double height = Math.Round(RenderSize.Height * sy, MidpointRounding.AwayFromZero);
            pixels = width > 0 && height > 0 ? new Rect(left, top, width, height) : Rect.Empty;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            GetFootprint(out Rect pixels, out Point origin, out double sx, out double sy);
            lastPixels = pixels;
            if (pixels.IsEmpty || Source is not BitmapSource original) return;
            int width = (int)pixels.Width, height = (int)pixels.Height;
            var candidates = original is BitmapFrame frame && frame.Decoder != null
                ? frame.Decoder.Frames.Cast<BitmapSource>() : [original];
            var input = SelectSource(candidates, width, height);
            if (input == null && MasterUri.Length > 0)
            {
                if (master == null)
                {
                    master = BitmapFrame.Create(new Uri(MasterUri), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    master.Freeze();
                }
                input = SelectSource([master], width, height);
            }
            // Beyond the supplied artwork's resolution, keep its pixels native rather than enlarge them.
            if (input == null)
            {
                input = candidates.OrderByDescending(bitmap => bitmap.PixelWidth * (long)bitmap.PixelHeight).First();
                if (master != null && master.PixelWidth > input.PixelWidth) input = master;
                width = Math.Min(width, input.PixelWidth);
                height = Math.Min(height, input.PixelHeight);
            }
            var bitmap = Rasterize(input, width, height);
            var rectangle = new Rect((pixels.X - origin.X) / sx, (pixels.Y - origin.Y) / sy,
                width / sx, height / sy);
            drawingContext.PushGuidelineSet(new GuidelineSet(
                [rectangle.Left, rectangle.Right], [rectangle.Top, rectangle.Bottom]));
            drawingContext.DrawImage(bitmap, rectangle);
            drawingContext.Pop();
        }

        internal static BitmapSource? SelectSource(IEnumerable<BitmapSource> candidates, int width, int height)
            => candidates.Where(bitmap => bitmap.PixelWidth >= width && bitmap.PixelHeight >= height)
                .OrderBy(bitmap => bitmap.PixelWidth * (long)bitmap.PixelHeight).FirstOrDefault();

        internal static BitmapSource Rasterize(BitmapSource source, int width, int height)
        {
            if (width < 1 || height < 1 || source.PixelWidth < width || source.PixelHeight < height)
                throw new ArgumentOutOfRangeException(nameof(width), "The icon source must cover the output pixels.");
            var cache = rasters.GetValue(source, _ => new RasterCache());
            long key = ((long)width << 32) | (uint)height;
            if (cache.Images.TryGetValue(key, out var cached)) return cached;
            var image = new Image { Source = source, Width = width, Height = height, Stretch = Stretch.Fill,
                UseLayoutRounding = true, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.Measure(new Size(width, height));
            image.Arrange(new Rect(0, 0, width, height));
            image.UpdateLayout();
            var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            result.Render(image);
            result.Freeze();
            if (cache.Images.Count >= 96) cache.Images.Clear();
            cache.Images.Add(key, result);
            return result;
        }
    }
}
