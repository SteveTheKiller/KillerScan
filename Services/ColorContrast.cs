using System;
using System.Windows.Media;

namespace KillerScan.Services
{
    /// <summary>WCAG contrast arithmetic for theme colors that are derived rather than stated.</summary>
    internal static class ColorContrast
    {
        /// <summary>The color moved toward white or black, whichever the background calls for,
        /// by the smallest step that reads at <paramref name="ratio"/> on <paramref name="background"/>.</summary>
        internal static Color ReadableOn(Color color, Color background, double ratio)
        {
            if (Contrast(color, background) >= ratio) return color;
            Color toward = Luminance(background) < 0.5 ? Colors.White : Colors.Black;
            for (double t = 0.05; t < 1; t += 0.05)
            {
                Color c = Mix(color, toward, t);
                if (Contrast(c, background) >= ratio) return c;
            }
            Color other = toward == Colors.White ? Colors.Black : Colors.White;
            return Contrast(toward, background) >= Contrast(other, background) ? toward : other;
        }

        internal static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        internal static double Luminance(Color c)
        {
            static double Channel(byte v) { double x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        private static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
            (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    }
}
