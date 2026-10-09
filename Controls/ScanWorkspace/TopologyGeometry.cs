using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

// The folder holds ScanWorkspace partials, and a KillerScan.Controls.ScanWorkspace namespace would
// collide with the ScanWorkspace class, so this file keeps the namespace of the code that uses it.
#pragma warning disable IDE0130
namespace KillerScan.Controls
#pragma warning restore IDE0130
{
    internal static class TopologyGeometry
    {
        internal const double MinimumZoom = 0.25;
        internal const double MaximumZoom = 3;

        internal static double WheelZoom(double zoom, int delta) =>
            Math.Max(MinimumZoom, Math.Min(MaximumZoom, zoom * Math.Pow(1.1, delta / 120.0)));

        internal static double AnchoredOffset(double offset, double pointer, double oldZoom, double newZoom) =>
            (offset + pointer) * newZoom / oldZoom - pointer;

        // Pack outward along elliptical rays, testing rectangular bounds rather than their
        // diagonals. A ray can stop as soon as its own box clears the reserved center and peers.
        internal static Point[] Radial(IReadOnlyList<Size> sizes, IReadOnlyList<Rect> reserved,
            double viewportWidth, double viewportHeight, double gap, bool horizontal)
        {
            double aspect = Math.Max(0.5, Math.Min(2.5, viewportWidth / Math.Max(1, viewportHeight)));
            Point[] best = [];
            double bestScore = double.MaxValue;
            foreach (double candidate in new[] { aspect * 0.65, aspect * 0.85, aspect })
            {
                var points = PackRadial(sizes, reserved, candidate, gap, horizontal);
                var bounds = Rect.Empty;
                foreach (var box in reserved) bounds.Union(box);
                for (int i = 0; i < points.Length; i++)
                    bounds.Union(new Rect(points[i].X - sizes[i].Width / 2, points[i].Y - sizes[i].Height / 2,
                        sizes[i].Width, sizes[i].Height));
                double score = Math.Max((bounds.Width + 4 * gap) / Math.Max(1, viewportWidth),
                    (bounds.Height + 4 * gap) / Math.Max(1, viewportHeight));
                if (score < bestScore)
                {
                    best = points;
                    bestScore = score;
                }
            }
            return best;
        }

        private static Point[] PackRadial(IReadOnlyList<Size> sizes, IReadOnlyList<Rect> reserved,
            double aspect, double gap, bool horizontal)
        {
            var placed = reserved.ToList();
            var points = new Point[sizes.Count];
            // Alternate rays leave gaps for the second pass to fill nearer the center,
            // instead of pushing every neighbor farther out on the same expanding ring.
            foreach (int index in Enumerable.Range(0, sizes.Count).Where(i => i % 2 == 0)
                .Concat(Enumerable.Range(0, sizes.Count).Where(i => i % 2 != 0)))
            {
                double angle = 2 * Math.PI * index / sizes.Count + (horizontal ? 0 : Math.PI / 2);
                var direction = new Vector(Math.Cos(angle) * Math.Sqrt(aspect), Math.Sin(angle) / Math.Sqrt(aspect));
                double distance = 0;
                while (true)
                {
                    var point = new Point(direction.X * distance, direction.Y * distance);
                    var box = new Rect(point.X - sizes[index].Width / 2, point.Y - sizes[index].Height / 2,
                        sizes[index].Width, sizes[index].Height);
                    var clearance = box;
                    clearance.Inflate(gap, gap);
                    if (placed.All(other => !clearance.IntersectsWith(other)))
                    {
                        points[index] = point;
                        placed.Add(box);
                        break;
                    }
                    distance += Math.Max(2, gap / 2);
                }
            }
            return points;
        }
    }
}
