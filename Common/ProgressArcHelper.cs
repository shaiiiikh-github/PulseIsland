using System;
using System.Windows;
using System.Windows.Media;

namespace PulseIsland.Common
{
    public static class ProgressArcHelper
    {
        public static PathGeometry BuildArcGeometry(double center, double radius, double progress)
        {
            var geometry = new PathGeometry();
            progress = Math.Clamp(progress, 0.001, 0.999);

            double startAngle = -90.0;
            double sweepAngle = progress * 360.0;
            double endAngle = startAngle + sweepAngle;

            double startRad = startAngle * Math.PI / 180.0;
            double endRad = endAngle * Math.PI / 180.0;

            Point startPoint = new Point(
                center + radius * Math.Cos(startRad),
                center + radius * Math.Sin(startRad));

            Point endPoint = new Point(
                center + radius * Math.Cos(endRad),
                center + radius * Math.Sin(endRad));

            bool isLargeArc = sweepAngle > 180.0;

            var figure = new PathFigure
            {
                StartPoint = startPoint,
                IsClosed = false,
                IsFilled = false
            };

            figure.Segments.Add(new ArcSegment
            {
                Point = endPoint,
                Size = new Size(radius, radius),
                IsLargeArc = isLargeArc,
                SweepDirection = SweepDirection.Clockwise
            });

            geometry.Figures.Add(figure);
            return geometry;
        }
    }
}

