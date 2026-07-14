using System;
using System.Windows;
using System.Windows.Media;

namespace TempMonitor;

public partial class CircularProgressBar : System.Windows.Controls.UserControl
{
    private readonly ArcSegment _progressArc = new() { SweepDirection = SweepDirection.Clockwise };
    private readonly PathFigure _progressFigure = new() { IsClosed = false, IsFilled = false };
    private readonly PathGeometry _progressGeometry = new();

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(CircularProgressBar),
            new PropertyMetadata(0d, OnVisualPropertyChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(CircularProgressBar),
            new PropertyMetadata(100d, OnVisualPropertyChanged));

    public static readonly DependencyProperty CenterTextProperty =
        DependencyProperty.Register(nameof(CenterText), typeof(string), typeof(CircularProgressBar),
            new PropertyMetadata("0%"));

    public static readonly DependencyProperty ValueSuffixProperty =
        DependencyProperty.Register(nameof(ValueSuffix), typeof(string), typeof(CircularProgressBar),
            new PropertyMetadata("%", OnVisualPropertyChanged));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(CircularProgressBar),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ArcStrokeThicknessProperty =
        DependencyProperty.Register(nameof(ArcStrokeThickness), typeof(double), typeof(CircularProgressBar),
            new PropertyMetadata(10d, OnVisualPropertyChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string CenterText
    {
        get => (string)GetValue(CenterTextProperty);
        set => SetValue(CenterTextProperty, value);
    }

    public string ValueSuffix
    {
        get => (string)GetValue(ValueSuffixProperty);
        set => SetValue(ValueSuffixProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public double ArcStrokeThickness
    {
        get => (double)GetValue(ArcStrokeThicknessProperty);
        set => SetValue(ArcStrokeThicknessProperty, value);
    }

    public CircularProgressBar()
    {
        InitializeComponent();

        _progressFigure.Segments.Add(_progressArc);
        _progressGeometry.Figures.Add(_progressFigure);
        ProgressPath.Data = _progressGeometry;

        SizeChanged += (_, _) => UpdateProgressArc();
        Loaded += (_, _) => UpdateProgressArc();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CircularProgressBar control)
        {
            control.CenterText = $"{control.Value:0.#}{control.ValueSuffix}";
            control.UpdateProgressArc();
        }
    }

    private void UpdateProgressArc()
    {
        double size = Math.Max(0, Math.Min(ActualWidth, ActualHeight));
        if (size <= 0 || Maximum <= 0)
        {
            ProgressPath.Visibility = Visibility.Hidden;
            return;
        }

        double progress = Math.Clamp(Value / Maximum, 0, 1);
        if (progress <= 0)
        {
            ProgressPath.Visibility = Visibility.Hidden;
            return;
        }

        ProgressPath.Visibility = Visibility.Visible;

        double strokeThickness = ArcStrokeThickness;
        ProgressPath.StrokeThickness = strokeThickness;

        double radius = (size - strokeThickness) / 2;
        System.Windows.Point center = new(size / 2, size / 2);
        double startAngle = -90;
        double endAngle = startAngle + (progress * 359.99);

        System.Windows.Point startPoint = PointOnCircle(center, radius, startAngle);
        System.Windows.Point endPoint = PointOnCircle(center, radius, endAngle);
        bool isLargeArc = progress > 0.5;

        _progressFigure.StartPoint = startPoint;
        _progressArc.Point = endPoint;
        _progressArc.Size = new System.Windows.Size(radius, radius);
        _progressArc.IsLargeArc = isLargeArc;
    }

    private static System.Windows.Point PointOnCircle(System.Windows.Point center, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180d;
        return new System.Windows.Point(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }
}
