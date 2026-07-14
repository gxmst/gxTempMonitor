using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace TempMonitor;

public partial class SparklineChart : System.Windows.Controls.UserControl
{
    private readonly List<double> _fallbackValues = new();
    private readonly PointCollection _linePoints = new();
    private readonly PointCollection _areaPoints = new();
    private readonly PolyLineSegment _areaSegment = new();
    private readonly PathFigure _areaFigure = new() { IsClosed = true, IsFilled = true };
    private readonly PathGeometry _areaGeometry = new();
    private IReadOnlyList<double>? _renderValues;

    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IEnumerable<double>), typeof(SparklineChart),
            new PropertyMetadata(null, OnValuesChanged));

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

        LinePath.Points = _linePoints;
        _areaSegment.Points = _areaPoints;
        _areaFigure.Segments.Add(_areaSegment);
        _areaGeometry.Figures.Add(_areaFigure);
        AreaPath.Data = _areaGeometry;

        Loaded += (_, _) => RenderChart();
        SizeChanged += (_, _) => RenderChart();
    }

    public void UpdateValues(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _renderValues = values;
        RenderChart();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SparklineChart chart)
        {
            return;
        }

        if (e.NewValue is IReadOnlyList<double> indexedValues)
        {
            chart._renderValues = indexedValues;
        }
        else if (e.NewValue is IEnumerable<double> values)
        {
            chart._fallbackValues.Clear();
            foreach (double value in values)
            {
                chart._fallbackValues.Add(value);
            }

            chart._renderValues = chart._fallbackValues;
        }
        else
        {
            chart._renderValues = null;
        }

        chart.RenderChart();
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
        IReadOnlyList<double>? values = _renderValues;
        int valueCount = values?.Count ?? 0;

        if (width <= 0 || chartHeight <= 0 || valueCount < 2 || values is null)
        {
            _linePoints.Clear();
            _areaPoints.Clear();
            return;
        }

        double min = values[0];
        double max = min;
        for (int i = 1; i < valueCount; i++)
        {
            double value = values[i];
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        if (Math.Abs(max - min) < 0.01)
        {
            max = min + 1;
        }

        double step = width / (valueCount - 1d);
        _linePoints.Clear();
        _areaPoints.Clear();
        _areaFigure.StartPoint = new System.Windows.Point(0, height);

        for (int i = 0; i < valueCount; i++)
        {
            double normalized = (values[i] - min) / (max - min);
            double x = i * step;
            double y = chartHeight - (normalized * chartHeight) + 2;
            var point = new System.Windows.Point(x, y);
            _linePoints.Add(point);
            _areaPoints.Add(point);
        }

        _areaPoints.Add(new System.Windows.Point(width, height));
    }
}
