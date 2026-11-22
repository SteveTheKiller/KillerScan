using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerScan.Controls;

namespace KillerScan.Services
{
    /// <summary>
    /// Trash Polka's window background ink for HTML reports, as two PNG data URIs: the ink with the accent marks taken
    /// out, and the accent's coverage as an alpha mask. The report lays the coverage over the ink filled with its own
    /// --accent, which is the same layer math TrashPolkaRaster uses in the app (Rest/(1 - M) under M tinted in the accent),
    /// so the report shows the app's piece in whatever accent it has. Built once per process at a fixed size on its own
    /// STA thread, so the command line can build it too; a failed build leaves the report without the ink.
    /// </summary>
    internal static class ReportWallpaper
    {
        /// <summary>The piece's size in CSS pixels; the report covers the window with it.</summary>
        internal const int Width = 1920, Height = 1080;

        internal sealed class Layers(string ink, string coverage)
        {
            internal string InkUri { get; } = ink;
            internal string CoverageUri { get; } = coverage;
        }

        private static readonly Lazy<Layers?> Built = new(BuildOnStaThread, LazyThreadSafetyMode.ExecutionAndPublication);

        internal static Layers? Get() => Built.Value;

        private static int _warming;

        /// <summary>Starts the one build in the background, so the first report rarely waits for it. Safe to call often.</summary>
        internal static void Prewarm()
        {
            if (Built.IsValueCreated || Interlocked.Exchange(ref _warming, 1) == 1) return;
            System.Threading.Tasks.Task.Run(() => Built.Value);
        }

        private static Layers? BuildOnStaThread()
        {
            Layers? result = null;
            var thread = new Thread(() =>
            {
                try { result = Build(); }
                catch (Exception) { result = null; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return thread.Join(TimeSpan.FromSeconds(60)) ? result : null;
        }

        private static Layers Build()
        {
            const int w = Width, h = Height;
            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            Render(target, Colors.Black);
            var ink = new byte[w * h * 4];
            target.CopyPixels(ink, w * 4, 0);
            target.Clear();
            Render(target, Colors.White);
            var light = new byte[w * h * 4];
            target.CopyPixels(light, w * 4, 0);

            // M is the accent's coverage (white render minus black render); the ink keeps Rest/(1 - M), then goes to
            // straight alpha because PNG is not premultiplied.
            var mask = new byte[w * h * 4];
            for (int i = 0; i < ink.Length; i += 4)
            {
                int m = light[i + 1] - ink[i + 1];
                if (m < 0) m = 0;
                if (m > 255) m = 255;
                mask[i + 3] = Step(m, 8);
                if (m == 255) { ink[i] = ink[i + 1] = ink[i + 2] = ink[i + 3] = 0; continue; }
                double k = 255.0 / (255 - m);
                int alpha = (int)Math.Round((ink[i + 3] - m) * k);
                if (alpha <= 0) { ink[i] = ink[i + 1] = ink[i + 2] = ink[i + 3] = 0; continue; }
                if (alpha > 255) alpha = 255;
                for (int c = 0; c < 3; c++)
                {
                    int premultiplied = Math.Min(alpha, (int)Math.Round(ink[i + c] * k));
                    ink[i + c] = Step(Math.Min(255, (int)Math.Round(premultiplied * 255.0 / alpha)), 16);
                }
                ink[i + 3] = Step(alpha, 8);
            }
            return new Layers(Png(ink, w, h), Png(mask, w, h));
        }

        // The ink shows at a quarter opacity, so coarser steps cannot be seen and let the PNGs compress to a fraction.
        private static byte Step(int value, int step) => (byte)Math.Min(255, (value + step / 2) / step * step);

        private static void Render(RenderTargetBitmap target, Color accent)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawDrawing(TrashPolkaInk.DrawFrozen(accent, new Size(Width, Height), TrashPolkaInk.BackgroundSeed));
            target.Render(visual);
        }

        private static string Png(byte[] bgra, int w, int h)
        {
            var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
    }
}
