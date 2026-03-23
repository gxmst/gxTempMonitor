using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace TempMonitor;

public partial class SparklineChart : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IEnumerable<double>), typeof(SparklineChart),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty StrokeBrushProperty =
        DependencyProperty.Register(nameof(StrokeBrush), typeof(System.Windows.Media.Brush), typeof(SparklineChart),
            new PropertyMetadata(System.Windows.Media.Brushes.DeepSkyBlue, OnVisualPropertyChanged));

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public System.Windows.Media.Brush StrokeBrush
    {
        get => (System.Windows.Media.Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public SparklineChart()
    {
        InitializeComponent();
        Loaded += (_, _) => RenderChart();
        SizeChanged += (_, _) => RenderChart();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SparklineChart chart)
        {
            chart.RenderChart();
        }
    }

    private void RenderChart()
    {
        LinePath.Stroke = StrokeBrush;
        double width = ActualWidth;
        double height = ActualHeight;
        double chartHeight = Math.Max(0, height - 4);

        double[] values = Values?.ToArray() ?? [];
        if (width <= 0 || chartHeight <= 0 || values.Length < 2)
        {
            LinePath.Points = new PointCollection();
            AreaPath.Data = Geometry.Empty;
            return;
        }

        double min = values.Min();
        double max = values.Max();
        if (Math.Abs(max - min) < 0.01)
        {
            max = min + 1;
        }

        double step = width / (values.Length - 1d);
        var points = new PointCollection(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            double normalized = (values[i] - min) / (max - min);
            double x = i * step;
            double y = chartHeight - (normalized * chartHeight) + 2;
            points.Add(new System.Windows.Point(x, y));
        }

        LinePath.Points = points;

        var figure = new PathFigure { StartPoint = new System.Windows.Point(0, height), IsClosed = true };
        foreach (System.Windows.Point point in points)
        {
            figure.Segments.Add(new LineSegment(point, true));
        }

        figure.Segments.Add(new LineSegment(new System.Windows.Point(width, height), true));
        AreaPath.Data = new PathGeometry([figure]);
    }
}
