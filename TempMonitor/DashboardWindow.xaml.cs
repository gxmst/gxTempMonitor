using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;

namespace TempMonitor;

public partial class DashboardWindow : Window
{
    private const int TrendCapacity = 48;

    private bool _allowClose;
    private FrameworkElement? _currentView;
    private readonly Queue<double> _cpuTrend = new();
    private readonly Queue<double> _gpuTrend = new();
    private readonly Queue<double> _ramTrend = new();
    private readonly Queue<double> _vramTrend = new();
    private readonly Queue<double> _upTrend = new();
    private readonly Queue<double> _downTrend = new();

    private static readonly System.Windows.Media.Brush WarningBrush = CreateBrush("#FFA500");
    private static readonly System.Windows.Media.Brush CriticalBrush = CreateBrush("#FF4444");
    private static readonly System.Windows.Media.Brush NormalBrush = CreateBrush("#F7F9FC");

    public DashboardWindow()
    {
        InitializeComponent();
        HardwareMonitorService.Instance.DataUpdated += OnDataUpdated;
        ApplySnapshot(HardwareMonitorService.Instance.LatestSnapshot);
        NavigationListBox.SelectedIndex = 0;
        _currentView = OverviewView;
        UpdateVisibleSection("Overview", animate: false);
    }

    public void PrepareForExit()
    {
        _allowClose = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        HardwareMonitorService.Instance.DataUpdated -= OnDataUpdated;
        base.OnClosing(e);
    }

    private void OnDataUpdated(HardwareSnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(HardwareSnapshot snapshot)
    {
        PushTrend(_cpuTrend, snapshot.CpuUsage);
        PushTrend(_gpuTrend, snapshot.GpuTemperature ?? 0);
        PushTrend(_ramTrend, snapshot.RamUsedGb);
        PushTrend(_vramTrend, snapshot.VramUsedGb ?? 0);
        PushTrend(_upTrend, snapshot.NetUploadBytesPerSecond);
        PushTrend(_downTrend, snapshot.NetDownloadBytesPerSecond);

        AnimateGauge(CpuGauge, snapshot.CpuUsage);
        CpuTempValueText.Text = snapshot.CpuTemperature.HasValue
            ? $"温度 {snapshot.CpuTemperature.Value:0.0} °C"
            : "温度 --";

        AnimateGauge(GpuGauge, snapshot.GpuUsagePercent);
        GpuTempValueText.Text = snapshot.GpuTemperature.HasValue
            ? $"{snapshot.GpuTemperature.Value:0.0} °C"
            : "-- °C";
        GpuTempValueText.Foreground = GetAlertBrush(snapshot.GpuTemperature ?? 0);
        GpuVramValueText.Text = snapshot.VramUsedGb.HasValue
            ? $"显存 {snapshot.VramUsedGb.Value:F1} GB"
            : "显存 --";

        AnimateGauge(RamGauge, snapshot.RamUsagePercent);
        RamUsedValueText.Text = $"{snapshot.RamUsedGb:F1} / {snapshot.TotalRamGb:F1} GB";
        RamPercentValueText.Text = $"使用率 {snapshot.RamUsagePercent:0.0} %";

        NetDownValueText.Text = $"↓ {FormatSpeed(snapshot.NetDownloadBytesPerSecond)}";
        NetUpValueText.Text = $"↑ {FormatSpeed(snapshot.NetUploadBytesPerSecond)}";
        float networkActivity = Math.Min(100, (snapshot.NetDownloadBytesPerSecond + snapshot.NetUploadBytesPerSecond) / 1024f / 1024f * 10f);
        AnimateProgressBar(NetActivityProgressBar, networkActivity);
        TrendCpuValueText.Text = $"{snapshot.CpuUsage:0.0} %";
        TrendGpuValueText.Text = snapshot.GpuTemperature.HasValue ? $"{snapshot.GpuTemperature.Value:0.0} °C" : "-- °C";
        TrendRamValueText.Text = $"{snapshot.RamUsedGb:F1} GB";
        TrendVramValueText.Text = snapshot.VramUsedGb.HasValue ? $"{snapshot.VramUsedGb.Value:F1} GB" : "--";
        TrendUpValueText.Text = FormatSpeed(snapshot.NetUploadBytesPerSecond);
        TrendDownValueText.Text = FormatSpeed(snapshot.NetDownloadBytesPerSecond);
        TrendCpuChart.Values = _cpuTrend.ToArray();
        TrendGpuChart.Values = _gpuTrend.ToArray();
        TrendRamChart.Values = _ramTrend.ToArray();
        TrendVramChart.Values = _vramTrend.ToArray();
        TrendUpChart.Values = _upTrend.ToArray();
        TrendDownChart.Values = _downTrend.ToArray();

        RamDetailUsedText.Text = $"{snapshot.RamUsedGb:F1} / {snapshot.TotalRamGb:F1} GB";
        RamDetailUsedText.Foreground = GetAlertBrush(snapshot.RamUsagePercent);
        RamDetailPercentText.Text = $"使用率 {snapshot.RamUsagePercent:0.0} %";
        RamDetailAvailableText.Text = $"{snapshot.RamAvailableGb:F1} GB";
        RamDetailTotalText.Text = $"总内存 {snapshot.TotalRamGb:F1} GB";
        RamDetailPeakText.Text = $"{snapshot.RamUsedMaxGb:F1} GB";
        RamDetailHeadroomText.Text = $"{snapshot.RamAvailableGb:F1} GB";
        RamDetailPercentChipText.Text = $"{snapshot.RamUsagePercent:0.0} %";
        RamDetailAvailChipText.Text = $"{snapshot.RamAvailableGb:F1} GB";
        RamDetailUsedFootText.Text = $"{snapshot.RamUsedGb:F1} GB";
        RamDetailAvailFootText.Text = $"{snapshot.RamAvailableGb:F1} GB";
        RamDetailPeakFootText.Text = $"{snapshot.RamUsedMaxGb:F1} GB";
        RamDetailPercentFootText.Text = $"{snapshot.RamUsagePercent:0.0} %";
        AnimateProgressBar(RamDetailUsageProgressBar, snapshot.RamUsagePercent);

        NetworkDetailDownText.Text = $"↓ {FormatSpeed(snapshot.NetDownloadBytesPerSecond)}";
        NetworkDetailUpText.Text = $"↑ {FormatSpeed(snapshot.NetUploadBytesPerSecond)}";
        NetworkDetailInterfaceText.Text = string.IsNullOrWhiteSpace(snapshot.NetworkInterfaceName)
            ? "未识别"
            : snapshot.NetworkInterfaceName;
        NetworkDetailTotalText.Text = $"总吞吐 {FormatSpeed(snapshot.NetTotalBytesPerSecond)}";
        NetworkDetailPeakUpText.Text = FormatSpeed(snapshot.NetUploadMaxBytesPerSecond);
        NetworkDetailPeakDownText.Text = FormatSpeed(snapshot.NetDownloadMaxBytesPerSecond);
        NetworkDetailDownChipText.Text = FormatSpeed(snapshot.NetDownloadBytesPerSecond);
        NetworkDetailUpChipText.Text = FormatSpeed(snapshot.NetUploadBytesPerSecond);
        NetworkDetailInterfaceFootText.Text = string.IsNullOrWhiteSpace(snapshot.NetworkInterfaceName) ? "未识别" : snapshot.NetworkInterfaceName;
        NetworkDetailTotalFootText.Text = FormatSpeed(snapshot.NetTotalBytesPerSecond);
        NetworkDetailDownFootText.Text = FormatSpeed(snapshot.NetDownloadBytesPerSecond);
        NetworkDetailUpFootText.Text = FormatSpeed(snapshot.NetUploadBytesPerSecond);
        AnimateProgressBar(NetworkDetailActivityProgressBar, networkActivity);

        CpuDetailUsageText.Text = $"{snapshot.CpuUsage:0.0} %";
        CpuDetailUsageText.Foreground = GetAlertBrush(snapshot.CpuUsage);
        CpuDetailClockText.Text = snapshot.CpuClockMhz.HasValue
            ? $"频率 {snapshot.CpuClockMhz.Value:0} MHz"
            : "频率 --";
        CpuDetailTemperatureText.Text = snapshot.CpuTemperature.HasValue
            ? $"{snapshot.CpuTemperature.Value:0.0} °C"
            : "-- °C";
        CpuDetailTemperatureText.Foreground = GetAlertBrush(snapshot.CpuTemperature ?? 0);
        CpuDetailPowerText.Text = snapshot.CpuPackagePowerWatts.HasValue
            ? $"功耗 {snapshot.CpuPackagePowerWatts.Value:0.0} W"
            : "功耗 --";
        string cpuClockText = snapshot.CpuClockMhz.HasValue ? $"{snapshot.CpuClockMhz.Value:0} MHz" : "--";
        string cpuPowerText = snapshot.CpuPackagePowerWatts.HasValue ? $"{snapshot.CpuPackagePowerWatts.Value:0.0} W" : "--";
        string cpuTempText = snapshot.CpuTemperature.HasValue ? $"{snapshot.CpuTemperature.Value:0.0} °C" : "-- °C";
        CpuDetailMaxUsageText.Text = $"{snapshot.CpuUsageMax:0.0} %";
        CpuDetailMaxTempText.Text = snapshot.CpuTemperatureMax.HasValue
            ? $"{snapshot.CpuTemperatureMax.Value:0.0} °C"
            : "-- °C";
        CpuDetailClockChipText.Text = cpuClockText;
        CpuDetailPowerChipText.Text = cpuPowerText;
        CpuDetailUsageFootText.Text = $"{snapshot.CpuUsage:0.0} %";
        CpuDetailTempFootText.Text = cpuTempText;
        CpuDetailPowerFootText.Text = cpuPowerText;
        CpuDetailFreqFootText.Text = cpuClockText;
        AnimateProgressBar(CpuDetailUsageProgressBar, snapshot.CpuUsage);

        GpuDetailUsageText.Text = $"{snapshot.GpuUsagePercent:0.0} %";
        GpuDetailUsageText.Foreground = GetAlertBrush(snapshot.GpuUsagePercent);
        GpuDetailVramText.Text = snapshot.VramUsedGb.HasValue
            ? $"显存 {snapshot.VramUsedGb.Value:F1} GB"
            : "显存 --";
        GpuDetailMemoryValueText.Text = FormatOptionalGb(snapshot.VramUsedGb);
        GpuDetailTemperatureText.Text = snapshot.GpuTemperature.HasValue
            ? $"{snapshot.GpuTemperature.Value:0.0} °C"
            : "-- °C";
        GpuDetailTemperatureText.Foreground = GetAlertBrush(snapshot.GpuTemperature ?? 0);
        GpuDetailPowerText.Text = snapshot.GpuPowerWatts.HasValue
            ? $"功耗 {snapshot.GpuPowerWatts.Value:0.0} W"
            : "功耗 --";
        GpuDetailFanText.Text = snapshot.GpuFanRpm.HasValue
            ? $"风扇 {snapshot.GpuFanRpm.Value:0} RPM"
            : "风扇 --";
        string gpuPowerText = snapshot.GpuPowerWatts.HasValue ? $"{snapshot.GpuPowerWatts.Value:0.0} W" : "--";
        string gpuFanText = snapshot.GpuFanRpm.HasValue ? $"{snapshot.GpuFanRpm.Value:0} RPM" : "--";
        string gpuTempText = snapshot.GpuTemperature.HasValue ? $"{snapshot.GpuTemperature.Value:0.0} °C" : "-- °C";
        GpuDetailMaxTempText.Text = snapshot.GpuTemperatureMax.HasValue
            ? $"{snapshot.GpuTemperatureMax.Value:0.0} °C"
            : "-- °C";
        GpuDetailFanChipText.Text = gpuFanText;
        GpuDetailPowerChipText.Text = gpuPowerText;
        GpuDetailUsageFootText.Text = $"{snapshot.GpuUsagePercent:0.0} %";
        GpuDetailTempFootText.Text = gpuTempText;
        GpuDetailPowerFootText.Text = gpuPowerText;
        GpuDetailFanFootText.Text = gpuFanText;
        AnimateProgressBar(GpuDetailUsageProgressBar, snapshot.GpuUsagePercent);
    }

    private static string FormatOptionalGb(float? value) => value.HasValue ? $"{value.Value:F1} GB" : "--";

    private static void PushTrend(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > TrendCapacity)
        {
            queue.Dequeue();
        }
    }

    private void AnimateGauge(CircularProgressBar gauge, double target)
    {
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromSeconds(0.35),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        gauge.BeginAnimation(CircularProgressBar.ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateProgressBar(System.Windows.Controls.ProgressBar progressBar, double target)
    {
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromSeconds(0.3),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        progressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static System.Windows.Media.Brush CreateBrush(string colorHex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Brush GetAlertBrush(float value)
    {
        if (value >= 90)
        {
            return CriticalBrush;
        }

        if (value >= 80)
        {
            return WarningBrush;
        }

        return NormalBrush;
    }

    private static string FormatSpeed(float bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return $"{bytesPerSecond:0.0}B";
        }

        float kb = bytesPerSecond / 1024;
        if (kb < 1024)
        {
            return $"{kb:0.0}K";
        }

        return $"{kb / 1024.0:0.1}M";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void NavigationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavigationListBox.SelectedItem is ListBoxItem item && item.Tag is string target)
        {
            UpdateVisibleSection(target, animate: true);
        }
    }

    private void UpdateVisibleSection(string target, bool animate)
    {
        FrameworkElement nextView = GetSection(target);
        if (!animate)
        {
            foreach (FrameworkElement view in GetAllViews())
            {
                view.Visibility = ReferenceEquals(view, nextView) ? Visibility.Visible : Visibility.Collapsed;
                view.Opacity = ReferenceEquals(view, nextView) ? 1 : 0;
                GetTranslateTransform(view).Y = 0;
            }

            _currentView = nextView;
            return;
        }

        if (ReferenceEquals(_currentView, nextView))
        {
            return;
        }

        FrameworkElement? previousView = _currentView;
        _currentView = nextView;

        if (previousView != null)
        {
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromSeconds(0.12));
            fadeOut.Completed += (_, _) => previousView.Visibility = Visibility.Collapsed;
            previousView.BeginAnimation(OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
        }

        TranslateTransform nextTransform = GetTranslateTransform(nextView);
        nextView.Visibility = Visibility.Visible;
        nextView.Opacity = 0;
        nextTransform.Y = 10;

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromSeconds(0.25),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slideIn = new DoubleAnimation
        {
            From = 10,
            To = 0,
            Duration = TimeSpan.FromSeconds(0.25),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        nextView.BeginAnimation(OpacityProperty, fadeIn, HandoffBehavior.SnapshotAndReplace);
        nextTransform.BeginAnimation(TranslateTransform.YProperty, slideIn, HandoffBehavior.SnapshotAndReplace);
    }

    private FrameworkElement GetSection(string target) => target switch
    {
        "Cpu" => CpuView,
        "Gpu" => GpuView,
        "Ram" => RamView,
        "Network" => NetworkView,
        _ => OverviewView
    };

    private FrameworkElement[] GetAllViews() => [OverviewView, CpuView, GpuView, RamView, NetworkView];

    private static TranslateTransform GetTranslateTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }
}
