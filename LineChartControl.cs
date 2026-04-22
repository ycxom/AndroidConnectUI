using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AndroidConnectUI
{
    public class LineChartControl : Canvas
    {
        private readonly List<double> _dataPoints = new();
        private const int MaxPoints = 60;

        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register("Stroke", typeof(Brush), typeof(LineChartControl),
                new PropertyMetadata(Brushes.White, OnVisualPropertyChanged));

        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register("Fill", typeof(Brush), typeof(LineChartControl),
                new PropertyMetadata(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), OnVisualPropertyChanged));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register("StrokeThickness", typeof(double), typeof(LineChartControl),
                new PropertyMetadata(2.0, OnVisualPropertyChanged));

        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LineChartControl)d).InvalidateVisual();
        }

        public void AddDataPoint(double value)
        {
            value = Math.Max(0, Math.Min(100, value));
            _dataPoints.Add(value);
            while (_dataPoints.Count > MaxPoints)
                _dataPoints.RemoveAt(0);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            if (_dataPoints.Count < 2) return;

            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0) return;

            double leftPadding = 28;
            double rightPadding = 4;
            double topPadding = 4;
            double bottomPadding = 4;
            double chartWidth = width - leftPadding - rightPadding;
            double chartHeight = height - topPadding - bottomPadding;

            if (chartWidth <= 0 || chartHeight <= 0) return;

            DrawGridLines(dc, leftPadding, topPadding, chartWidth, chartHeight);
            DrawChart(dc, leftPadding, topPadding, chartWidth, chartHeight);
        }

        private void DrawGridLines(DrawingContext dc, double left, double top, double width, double height)
        {
            var gridBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
            var textBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
            var typeface = new Typeface("Segoe UI");

            int[] percentages = { 100, 75, 50, 25, 0 };
            foreach (int pct in percentages)
            {
                double y = top + height * (1 - pct / 100.0);
                dc.DrawLine(new Pen(gridBrush, 0.5), new Point(left, y), new Point(left + width, y));

                var formattedText = new FormattedText(
                    $"{pct}%",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    9,
                    textBrush,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                dc.DrawText(formattedText, new Point(0, y - formattedText.Height / 2));
            }
        }

        private void DrawChart(DrawingContext dc, double left, double top, double width, double height)
        {
            var points = new Point[_dataPoints.Count];
            double xStep = width / (MaxPoints - 1);

            for (int i = 0; i < _dataPoints.Count; i++)
            {
                double x = left + (MaxPoints - _dataPoints.Count + i) * xStep;
                double y = top + height * (1 - _dataPoints[i] / 100.0);
                points[i] = new Point(x, y);
            }

            var fillFigure = new PathFigure();
            fillFigure.StartPoint = new Point(points[0].X, top + height);
            fillFigure.Segments.Add(new LineSegment(points[0], true));
            for (int i = 1; i < points.Length; i++)
            {
                fillFigure.Segments.Add(new LineSegment(points[i], true));
            }
            fillFigure.Segments.Add(new LineSegment(new Point(points[points.Length - 1].X, top + height), true));
            fillFigure.IsClosed = true;

            var fillGeometry = new PathGeometry();
            fillGeometry.Figures.Add(fillFigure);
            dc.DrawGeometry(Fill, null, fillGeometry);

            var lineFigure = new PathFigure();
            lineFigure.StartPoint = points[0];
            for (int i = 1; i < points.Length; i++)
            {
                lineFigure.Segments.Add(new LineSegment(points[i], true));
            }

            var lineGeometry = new PathGeometry();
            lineGeometry.Figures.Add(lineFigure);
            dc.DrawGeometry(null, new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round }, lineGeometry);
        }
    }
}
