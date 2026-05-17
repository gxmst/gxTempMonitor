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

        ApplyOverviewSnapshot(snapshot);
        ApplyCpuDetailSnapshot(snapshot);
        ApplyGpuDetailSnapshot(snapshot);
        ApplyRamDetailSnapshot(snapshot);
        ApplyNetworkDetailSnapshot(snapshot);
        ApplyTrendSnapshot(snapshot);
    }

    private void ApplyOverviewSnapshot(HardwareSnapshot s)
    {
        AnimateGauge(CpuGauge, s.CpuUsage);
        CpuTempValueText.Text = "温度 --";

        AnimateGauge(GpuGauge, s.GpuUsagePercent);
        GpuTempValueText.Text = UiHelper.FormatOptionalTemp(s.GpuTemperature);
        GpuTempValueText.Foreground = UiHelper.GetAlertBrush(s.GpuTemperature ?? 0);
        GpuVramValueText.Text = s.VramUsedGb.HasValue
            ? $"显存 {s.VramUsedGb.Value:F1} GB"
            : "显存 --";

        AnimateGauge(RamGauge, s.RamUsagePercent);
        RamUsedValueText.Text = $"{s.RamUsedGb:F1} / {s.TotalRamGb:F1} GB";
        RamPercentValueText.Text = $"使用率 {s.RamUsagePercent:0.0} %";

        NetDownValueText.Text = $"↓ {UiHelper.FormatSpeed(s.NetDownloadBytesPerSecond)}";
        NetUpValueText.Text = $"↑ {UiHelper.FormatSpeed(s.NetUploadBytesPerSecond)}";
        float networkActivity = Math.Min(100, (s.NetDownloadBytesPerSecond + s.NetUploadBytesPerSecond) / 1024f / 1024f * 10f);
        AnimateProgressBar(NetActivityProgressBar, networkActivity);
    }

    private void ApplyTrendSnapshot(HardwareSnapshot s)
    {
        TrendCpuValueText.Text = $"{s.CpuUsage:0.0} %";
        TrendGpuValueText.Text = UiHelper.FormatOptionalTemp(s.GpuTemperature);
        TrendRamValueText.Text = $"{s.RamUsedGb:F1} GB";
        TrendVramValueText.Text = s.VramUsedGb.HasValue ? $"{s.VramUsedGb.Value:F1} GB" : "--";
        TrendUpValueText.Text = UiHelper.FormatSpeed(s.NetUploadBytesPerSecond);
        TrendDownValueText.Text = UiHelper.FormatSpeed(s.NetDownloadBytesPerSecond);
        TrendCpuChart.Values = _cpuTrend.ToArray();
        TrendGpuChart.Values = _gpuTrend.ToArray();
        TrendRamChart.Values = _ramTrend.ToArray();
        TrendVramChart.Values = _vramTrend.ToArray();
        TrendUpChart.Values = _upTrend.ToArray();
        TrendDownChart.Values = _downTrend.ToArray();
    }

    private void ApplyCpuDetailSnapshot(HardwareSnapshot s)
    {
        CpuDetailUsageText.Text = $"{s.CpuUsage:0.0} %";
        CpuDetailUsageText.Foreground = UiHelper.GetAlertBrush(s.CpuUsage);
        CpuDetailClockText.Text = "频率 --";
        CpuDetailTemperatureText.Text = "--";
        CpuDetailPowerText.Text = "功耗 --";

        CpuDetailMaxUsageText.Text = $"{s.CpuUsageMax:0.0} %";
        CpuDetailMaxTempText.Text = "--";
        CpuDetailClockChipText.Text = "--";
        CpuDetailPowerChipText.Text = "--";
        CpuDetailUsageFootText.Text = $"{s.CpuUsage:0.0} %";
        CpuDetailTempFootText.Text = "--";
        CpuDetailPowerFootText.Text = "--";
        CpuDetailFreqFootText.Text = "--";
        AnimateProgressBar(CpuDetailUsageProgressBar, s.CpuUsage);
    }

    private void ApplyGpuDetailSnapshot(HardwareSnapshot s)
    {
        GpuDetailUsageText.Text = $"{s.GpuUsagePercent:0.0} %";
        GpuDetailUsageText.Foreground = UiHelper.GetAlertBrush(s.GpuUsagePercent);
        GpuDetailVramText.Text = s.VramUsedGb.HasValue
            ? $"显存 {s.VramUsedGb.Value:F1} GB"
            : "显存 --";
        GpuDetailMemoryValueText.Text = UiHelper.FormatOptionalGb(s.VramUsedGb);
        GpuDetailTemperatureText.Text = UiHelper.FormatOptionalTemp(s.GpuTemperature);
        GpuDetailTemperatureText.Foreground = UiHelper.GetAlertBrush(s.GpuTemperature ?? 0);
        GpuDetailPowerText.Text = s.GpuPowerWatts.HasValue
            ? $"功耗 {s.GpuPowerWatts.Value:0.0} W"
            : "功耗 --";
        GpuDetailFanText.Text = "风扇 --";

        string gpuPowerText = s.GpuPowerWatts.HasValue ? $"{s.GpuPowerWatts.Value:0.0} W" : "--";
        string gpuFanText = "--";
        string gpuTempText = UiHelper.FormatOptionalTemp(s.GpuTemperature, "--");

        GpuDetailMaxTempText.Text = UiHelper.FormatOptionalTemp(s.GpuTemperatureMax);
        GpuDetailFanChipText.Text = gpuFanText;
        GpuDetailPowerChipText.Text = gpuPowerText;
        GpuDetailUsageFootText.Text = $"{s.GpuUsagePercent:0.0} %";
        GpuDetailTempFootText.Text = gpuTempText;
        GpuDetailPowerFootText.Text = gpuPowerText;
        GpuDetailFanFootText.Text = gpuFanText;
        AnimateProgressBar(GpuDetailUsageProgressBar, s.GpuUsagePercent);
    }

    private void ApplyRamDetailSnapshot(HardwareSnapshot s)
    {
        RamDetailUsedText.Text = $"{s.RamUsedGb:F1} / {s.TotalRamGb:F1} GB";
        RamDetailUsedText.Foreground = UiHelper.GetAlertBrush(s.RamUsagePercent);
        RamDetailPercentText.Text = $"使用率 {s.RamUsagePercent:0.0} %";
        RamDetailAvailableText.Text = $"{s.RamAvailableGb:F1} GB";
        RamDetailTotalText.Text = $"总内存 {s.TotalRamGb:F1} GB";
        RamDetailPeakText.Text = $"{s.RamUsedMaxGb:F1} GB";
        RamDetailHeadroomText.Text = $"{s.RamAvailableGb:F1} GB";
        RamDetailPercentChipText.Text = $"{s.RamUsagePercent:0.0} %";
        RamDetailAvailChipText.Text = $"{s.RamAvailableGb:F1} GB";
        RamDetailUsedFootText.Text = $"{s.RamUsedGb:F1} GB";
        RamDetailAvailFootText.Text = $"{s.RamAvailableGb:F1} GB";
        RamDetailPeakFootText.Text = $"{s.RamUsedMaxGb:F1} GB";
        RamDetailPercentFootText.Text = $"{s.RamUsagePercent:0.0} %";
        AnimateProgressBar(RamDetailUsageProgressBar, s.RamUsagePercent);
    }

    private void ApplyNetworkDetailSnapshot(HardwareSnapshot s)
    {
        NetworkDetailDownText.Text = $"↓ {UiHelper.FormatSpeed(s.NetDownloadBytesPerSecond)}";
        NetworkDetailUpText.Text = $"↑ {UiHelper.FormatSpeed(s.NetUploadBytesPerSecond)}";
        NetworkDetailInterfaceText.Text = string.IsNullOrWhiteSpace(s.NetworkInterfaceName)
            ? "未识别"
            : s.NetworkInterfaceName;
        NetworkDetailTotalText.Text = $"总吞吐 {UiHelper.FormatSpeed(s.NetTotalBytesPerSecond)}";
        NetworkDetailPeakUpText.Text = UiHelper.FormatSpeed(s.NetUploadMaxBytesPerSecond);
        NetworkDetailPeakDownText.Text = UiHelper.FormatSpeed(s.NetDownloadMaxBytesPerSecond);
        NetworkDetailDownChipText.Text = UiHelper.FormatSpeed(s.NetDownloadBytesPerSecond);
        NetworkDetailUpChipText.Text = UiHelper.FormatSpeed(s.NetUploadBytesPerSecond);
        NetworkDetailInterfaceFootText.Text = string.IsNullOrWhiteSpace(s.NetworkInterfaceName) ? "未识别" : s.NetworkInterfaceName;
        NetworkDetailTotalFootText.Text = UiHelper.FormatSpeed(s.NetTotalBytesPerSecond);
        NetworkDetailDownFootText.Text = UiHelper.FormatSpeed(s.NetDownloadBytesPerSecond);
        NetworkDetailUpFootText.Text = UiHelper.FormatSpeed(s.NetUploadBytesPerSecond);
        float networkActivity = Math.Min(100, (s.NetDownloadBytesPerSecond + s.NetUploadBytesPerSecond) / 1024f / 1024f * 10f);
        AnimateProgressBar(NetworkDetailActivityProgressBar, networkActivity);
    }

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
