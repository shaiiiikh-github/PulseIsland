using System;
using System.Windows;
using System.Windows.Media;

namespace PulseIsland.Common
{
    /// <summary>
    /// Generates continuous-curvature squircle (superellipse) geometries with G2 continuity.
    /// Supports both Island mode (all 4 corners rounded) and Notch mode (top corners flat, bottom corners rounded).
    /// </summary>
    public static class SquircleHelper
    {
        public static PathGeometry BuildSquircleGeometry(double width, double height, double radius, bool isNotchMode = false)
        {
            var geometry = new PathGeometry();
            if (width <= 0 || height <= 0) return geometry;

            double maxRadius = Math.Min(width, height) / 2.0;
            radius = Math.Clamp(radius, 0.0, maxRadius);

            var figure = new PathFigure
            {
                IsClosed = true,
                IsFilled = true
            };

            if (isNotchMode)
            {
                // Notch mode: Top edge is flush (0,0) to (width, 0)
                figure.StartPoint = new Point(0, 0);
                figure.Segments.Add(new LineSegment(new Point(width, 0), true));

                // Right vertical edge down to bottom-right squircle start
                double bR = Math.Min(radius, Math.Min(width / 2.0, height));
                double bL = bR * 1.5286;
                if (bL > width / 2.0) bL = width / 2.0;
                if (bL > height) bL = height;

                figure.Segments.Add(new LineSegment(new Point(width, height - bL), true));

                // Bottom-Right Squircle Corner
                AppendSquircleCorner(figure,
                    new Point(width, height - bL),
                    new Point(width, height),
                    new Point(width - bL, height),
                    bL);

                // Bottom straight edge
                figure.Segments.Add(new LineSegment(new Point(bL, height), true));

                // Bottom-Left Squircle Corner
                AppendSquircleCorner(figure,
                    new Point(bL, height),
                    new Point(0, height),
                    new Point(0, height - bL),
                    bL);

                // Left vertical edge back to (0,0)
                figure.Segments.Add(new LineSegment(new Point(0, 0), true));
            }
            else
            {
                // Island mode: Full continuous curvature squircle
                double L = Math.Min(radius * 1.5286, Math.Min(width / 2.0, height / 2.0));

                figure.StartPoint = new Point(L, 0);

                // Top straight segment
                figure.Segments.Add(new LineSegment(new Point(width - L, 0), true));

                // Top-Right Squircle Corner
                AppendSquircleCorner(figure,
                    new Point(width - L, 0),
                    new Point(width, 0),
                    new Point(width, L),
                    L);

                // Right straight segment
                figure.Segments.Add(new LineSegment(new Point(width, height - L), true));

                // Bottom-Right Squircle Corner
                AppendSquircleCorner(figure,
                    new Point(width, height - L),
                    new Point(width, height),
                    new Point(width - L, height),
                    L);

                // Bottom straight segment
                figure.Segments.Add(new LineSegment(new Point(L, height), true));

                // Bottom-Left Squircle Corner
                AppendSquircleCorner(figure,
                    new Point(L, height),
                    new Point(0, height),
                    new Point(0, height - L),
                    L);

                // Left straight segment
                figure.Segments.Add(new LineSegment(new Point(0, L), true));

                // Top-Left Squircle Corner
                AppendSquircleCorner(figure,
                    new Point(0, L),
                    new Point(0, 0),
                    new Point(L, 0),
                    L);
            }

            geometry.Figures.Add(figure);
            return geometry;
        }

        private static void AppendSquircleCorner(
            PathFigure figure,
            Point startPoint,
            Point cornerPoint,
            Point endPoint,
            double L)
        {
            // Apple continuous curvature approximation:
            // 3-point cubic bezier transition smoothly ramping curvature to/from zero
            // Points calculated relative to start, corner, and end
            Vector toCorner = cornerPoint - startPoint;
            Vector fromCorner = endPoint - cornerPoint;

            Point cp1 = startPoint + (toCorner * 0.448);
            Point cp2 = endPoint - (fromCorner * 0.448);

            figure.Segments.Add(new BezierSegment(cp1, cp2, endPoint, true));
        }
    }
}

